namespace FileComparer.Application.Compare;

internal sealed class PathDisjointSet
{
    private readonly Dictionary<string, string> parents = new(
        StringComparer.OrdinalIgnoreCase);

    public void Add(string path) => parents.TryAdd(path, path);

    public string Find(string path)
    {
        var root = path;
        while (!string.Equals(
                   root,
                   parents[root],
                   StringComparison.OrdinalIgnoreCase))
        {
            root = parents[root];
        }

        while (!string.Equals(
                   path,
                   root,
                   StringComparison.OrdinalIgnoreCase))
        {
            var parent = parents[path];
            parents[path] = root;
            path = parent;
        }

        return root;
    }

    public void Union(string left, string right)
    {
        var leftRoot = Find(left);
        var rightRoot = Find(right);
        if (!string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase))
        {
            parents[rightRoot] = leftRoot;
        }
    }
}
