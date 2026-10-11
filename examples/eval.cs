return new {
    unity = UnityEngine.Application.unityVersion,
    roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount
};
