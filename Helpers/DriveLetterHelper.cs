using System.IO;

namespace DriveLink.Helpers;

public static class DriveLetterHelper
{
    private static readonly char[] Candidates =
        Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).ToArray();

    public static char? GetNextAvailable(IEnumerable<char>? alreadyReserved = null)
    {
        var used = GetUsed(alreadyReserved);
        foreach (var c in Candidates)
            if (!used.Contains(c))
                return c;
        return null;
    }

    public static IReadOnlyList<char> GetAvailable(IEnumerable<char>? alreadyReserved = null)
    {
        var used = GetUsed(alreadyReserved);
        return Candidates.Where(c => !used.Contains(c)).ToList();
    }

    private static HashSet<char> GetUsed(IEnumerable<char>? alreadyReserved)
    {
        var used = DriveInfo.GetDrives()
                            .Select(d => char.ToUpper(d.Name[0]))
                            .ToHashSet();
        if (alreadyReserved != null)
            foreach (var c in alreadyReserved)
                used.Add(char.ToUpper(c));
        return used;
    }
}
