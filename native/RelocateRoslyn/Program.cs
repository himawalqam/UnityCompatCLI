using Mono.Cecil;

string input=args[0],output=args[1];
var files=Directory.GetDirectories(input).Where(x=>!Path.GetFileName(x).Equals("mono.cecil"))
    .SelectMany(x=>Directory.GetFiles(Path.Combine(x,"lib","netstandard2.0"),"*.dll")).ToArray();
var resolver=new RelocatingResolver(); foreach(var file in files) resolver.AddSearchDirectory(Path.GetDirectoryName(file));
resolver.AddSearchDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
var modules=files.Select(x=>AssemblyDefinition.ReadAssembly(x,new ReaderParameters{AssemblyResolver=resolver,MetadataResolver=new RelocatingMetadataResolver(resolver),ReadingMode=ReadingMode.Immediate})).ToArray();
var names=modules.ToDictionary(x=>x.Name.Name,x=>x.Name.Version);
foreach(var assembly in modules) {resolver.Loaded[assembly.Name.Name]=assembly;resolver.Loaded["UnityCompat."+assembly.Name.Name]=assembly;}
foreach(var assembly in modules)
{
    var module=assembly.MainModule;
    foreach(var type in module.GetTypeReferences())
        if((type.Scope == module || type.Scope is AssemblyNameReference scope && names.ContainsKey(scope.Name)) && !String.IsNullOrEmpty(type.Namespace))
            type.Namespace="UnityCompat.Compiler."+type.Namespace;
    // Type definitions include nested types. Only nonempty namespaces need relocating.
    void Relocate(TypeDefinition type)
    {if(!String.IsNullOrEmpty(type.Namespace)) type.Namespace="UnityCompat.Compiler."+type.Namespace;
        foreach(var nested in type.NestedTypes) Relocate(nested);}
    foreach(var type in module.Types) Relocate(type);
    var resources=new Dictionary<string,string>();
    foreach(var resource in module.Resources.Where(r=>r.Name.EndsWith(".resources")))
    {
        string original=resource.Name; resource.Name="UnityCompat.Compiler."+original;
        resources[original[..^10]]=resource.Name[..^10];
    }
    foreach(var type in module.GetTypes()) foreach(var method in type.Methods.Where(m=>m.HasBody))
        foreach(var instruction in method.Body.Instructions)
            if(instruction.OpCode==Mono.Cecil.Cil.OpCodes.Ldstr && instruction.Operand is string literal && resources.TryGetValue(literal,out var relocated))
                instruction.Operand=relocated;
    foreach(var reference in module.AssemblyReferences)
        if(names.TryGetValue(reference.Name,out var version))
        {reference.Name="UnityCompat."+reference.Name;reference.Version=version;reference.PublicKeyToken=Array.Empty<byte>();}
    foreach(var attr in assembly.CustomAttributes.Where(a=>a.AttributeType.FullName=="System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
    {
        string friend=(string)attr.ConstructorArguments[0].Value; string name=friend.Split(',')[0];
        if(names.ContainsKey(name)) attr.ConstructorArguments[0]=new CustomAttributeArgument(module.TypeSystem.String,"UnityCompat."+name);
    }
    assembly.Name.Name="UnityCompat."+assembly.Name.Name;assembly.Name.PublicKey=Array.Empty<byte>();assembly.Name.HasPublicKey=false;
    module.Name=assembly.Name.Name+".dll"; module.Attributes &= ~ModuleAttributes.StrongNameSigned;
}
foreach(var assembly in modules)
{
    var module=assembly.MainModule;
    string path=Path.Combine(output,module.Name);assembly.Write(path);
    // Independently authored Editor-only importer settings; no Pipeline metadata is copied.
    string guid=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(module.Name))).ToLowerInvariant()[..32];
    File.WriteAllText(path+".meta",$"fileFormatVersion: 2\nguid: {guid}\nPluginImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  isPreloaded: 0\n  isExplicitlyReferenced: 1\n  validateReferences: 1\n  platformData:\n  - first:\n      Any: \n    second:\n      enabled: 0\n      settings: {{}}\n  - first:\n      Editor: Editor\n    second:\n      enabled: 1\n      settings:\n        CPU: AnyCPU\n        OS: AnyOS\n");
    Console.WriteLine(module.Name);
}

sealed class RelocatingResolver : DefaultAssemblyResolver
{
    internal readonly Dictionary<string,AssemblyDefinition> Loaded=new();
    public override AssemblyDefinition Resolve(AssemblyNameReference name) => Loaded.TryGetValue(name.Name,out var a)?a:base.Resolve(name);
    public override AssemblyDefinition Resolve(AssemblyNameReference name,ReaderParameters parameters) => Loaded.TryGetValue(name.Name,out var a)?a:base.Resolve(name,parameters);
}
sealed class RelocatingMetadataResolver(RelocatingResolver resolver) : MetadataResolver(resolver)
{
    public override TypeDefinition Resolve(TypeReference reference)
    {
        var type=reference.GetElementType();
        ModuleDefinition module=type.Scope as ModuleDefinition;
        if(type.Scope is AssemblyNameReference name && resolver.Loaded.TryGetValue(name.Name,out var assembly)) module=assembly.MainModule;
        if(module!=null && resolver.Loaded.Values.Any(a=>a.MainModule==module))
            return module.GetTypes().FirstOrDefault(t=>t.FullName==type.FullName);
        return base.Resolve(reference);
    }
}
