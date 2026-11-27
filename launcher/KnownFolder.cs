using System.Runtime.InteropServices;

namespace GlazeLauncher;

internal sealed record KnownFolder(Guid Id, string Name, string Path)
{
    // Windows KNOWNFOLDERIDs are stable across user accounts, Windows languages and redirected paths.
    private static readonly (Guid Id, string Name)[] UserFolders =
    [
        (new Guid("5e6c858f-0e22-4760-9afe-ea3317b67173"), "Home"),
        (new Guid("b4bfcc3a-db2c-424c-b029-7fe99a87c641"), "Desktop"),
        (new Guid("374de290-123f-4565-9164-39c4925e467b"), "Downloads"),
        (new Guid("fdd39ad0-238f-46af-adb4-6c85480369c7"), "Documents"),
        (new Guid("33e28130-4e1e-4676-835a-98395c3bc3bb"), "Pictures"),
        (new Guid("4bd8d571-6d19-48d3-be97-422220080e43"), "Music"),
        (new Guid("18989b1d-99b5-455b-841c-ab7c74e4ddfc"), "Videos"),
    ];

    public static List<KnownFolder> Enumerate()
    {
        var folders = new List<KnownFolder>(UserFolders.Length);
        foreach (var (id, name) in UserFolders)
        {
            // Default flags verify the current path and allow Windows folder redirection.
            int result = SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var pathPointer);
            try
            {
                if (result >= 0 && Marshal.PtrToStringUni(pathPointer) is { Length: > 0 } path)
                    folders.Add(new KnownFolder(id, name, path));
            }
            finally { Marshal.FreeCoTaskMem(pathPointer); }
        }
        return folders;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
