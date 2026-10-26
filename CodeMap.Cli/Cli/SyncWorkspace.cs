namespace CodeMap.Cli.Cli;

/// <summary>A failure to stage or publish an index, with a message meant to be shown to the person running `sync`.</summary>
internal sealed class SyncPublishException(string message) : Exception(message);

/// <summary>
/// Where one `sync` of one project builds its result, and how that result replaces the previous one.
///
/// The pipeline runs in a staging directory that sits NEXT to Output (so a rename into place never crosses a
/// volume). Only when the required steps are done does the finished <c>index/</c> replace Output's: the old one
/// is renamed aside, the new one renamed in, and the old one deleted last — so there is always either the old
/// index or the new one on disk, and a failed swap is rolled back. Everything else in Output (logs/, anything a
/// person put there) is never touched; MAP.md and modules/ are replaced file by file after the index.
///
/// A lock next to Output keeps a second `sync` of the same Output out: the nightly task and a person running it
/// by hand would otherwise delete each other's staging directory.
/// </summary>
internal sealed class SyncWorkspace : IDisposable
{
    private const string IndexName = "index";

    private readonly FileStream _lock;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    private SyncWorkspace(string outDir, string stagingDir, FileStream @lock, IReadOnlyList<TimeSpan> retryDelays)
    {
        OutDir = outDir;
        StagingDir = stagingDir;
        _lock = @lock;
        _retryDelays = retryDelays;
    }

    public string OutDir { get; }
    public string StagingDir { get; }
    public string StagingIndexDir => Path.Combine(StagingDir, IndexName);
    private string IndexDir => Path.Combine(OutDir, IndexName);

    /// <summary>Takes the lock, repairs what a killed run left behind, and creates an empty staging directory seeded with Output's MAP.md and modules/.</summary>
    public static SyncWorkspace Open(string outDir, IReadOnlyList<TimeSpan> retryDelays)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
        var parent = Path.GetDirectoryName(full)
            ?? throw new SyncPublishException($"{full} is a filesystem root; the output directory needs a parent to stage next to.");
        var name = Path.GetFileName(full);

        FileStream lockFile;
        try
        {
            Directory.CreateDirectory(parent);
            lockFile = new FileStream(
                Path.Combine(parent, name + ".sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new SyncPublishException(
                $"another `codemap sync` is already writing to {full} ({ex.Message}). Wait for it to finish, or delete {name}.sync.lock if no sync is running.");
        }

        try
        {
            RecoverInterruptedPublish(full);
            foreach (var leftover in Directory.GetDirectories(parent, name + ".sync-staging-*"))
                TryDelete(leftover, "a staging directory left by an earlier run");

            var staging = Path.Combine(parent, $"{name}.sync-staging-{Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(staging);
            var workspace = new SyncWorkspace(full, staging, lockFile, retryDelays);
            workspace.SeedDocsFromOutput();
            return workspace;
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// `map` keeps the text between the human markers by reading the MAP.md and modules/*.md it is about to
    /// overwrite. It now overwrites copies in staging, so those copies have to be there to be read.
    /// </summary>
    private void SeedDocsFromOutput()
    {
        var map = Path.Combine(OutDir, "MAP.md");
        if (File.Exists(map)) File.Copy(map, Path.Combine(StagingDir, "MAP.md"));

        var modules = Path.Combine(OutDir, "modules");
        if (!Directory.Exists(modules)) return;

        var stagedModules = Directory.CreateDirectory(Path.Combine(StagingDir, "modules")).FullName;
        foreach (var file in Directory.GetFiles(modules))
            File.Copy(file, Path.Combine(stagedModules, Path.GetFileName(file)));
    }

    /// <summary>
    /// A process killed between the two renames of <see cref="PublishIndex"/> leaves no index/ at all and the old
    /// one parked as index.old-*. Put it back, so a failed or interrupted next run does not leave readers with
    /// nothing; any other parked copy is just garbage from a cleanup that did not finish.
    /// </summary>
    private static void RecoverInterruptedPublish(string outDir)
    {
        if (!Directory.Exists(outDir)) return;

        var parked = Directory.GetDirectories(outDir, IndexName + ".old-*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToList();
        var skip = 0;
        var index = Path.Combine(outDir, IndexName);
        if (parked.Count > 0 && !Directory.Exists(index))
        {
            try
            {
                Directory.Move(parked[0], index);
                Console.WriteLine($"  restored the previous index from an interrupted publish ({parked[0]})");
                skip = 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  could not restore {parked[0]} to {index}: {ex.Message}");
                return;
            }
        }

        foreach (var extra in parked.Skip(skip))
            TryDelete(extra, "a replaced index left by an earlier run");
    }

    /// <summary>
    /// The commit point: swaps staging/index in for Output/index. On any failure the previous index is back in
    /// place (or, if even that fails, its location is in the message) and nothing half-new is left behind.
    /// </summary>
    public void PublishIndex()
    {
        try
        {
            Directory.CreateDirectory(OutDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncPublishException($"could not create {OutDir}: {ex.Message}");
        }

        var parked = Path.Combine(OutDir, $"{IndexName}.old-{Guid.NewGuid().ToString("N")[..8]}");
        var hadPrevious = Directory.Exists(IndexDir);

        if (hadPrevious)
            Retry(() => Directory.Move(IndexDir, parked), "set the previous index aside");

        try
        {
            Retry(() => Directory.Move(StagingIndexDir, IndexDir), "move the new index into place");
        }
        catch (SyncPublishException failure)
        {
            if (!hadPrevious) throw;

            try
            {
                Retry(() => Directory.Move(parked, IndexDir), "put the previous index back");
            }
            catch (SyncPublishException rollback)
            {
                throw new SyncPublishException(
                    $"{failure.Message} The previous index could not be put back either ({rollback.Message}); " +
                    $"it is at {parked} — move it to {IndexDir} by hand.");
            }

            throw new SyncPublishException($"{failure.Message} The previous index was restored.");
        }

        if (hadPrevious) TryDelete(parked, "the replaced index");
    }

    /// <summary>MAP.md and modules/*.md, replaced one file at a time (each replace is atomic) after the index is already in place.</summary>
    public void PublishDocs()
    {
        var staged = new List<(string Source, string Destination)>();
        var map = Path.Combine(StagingDir, "MAP.md");
        if (File.Exists(map)) staged.Add((map, Path.Combine(OutDir, "MAP.md")));

        var modules = Path.Combine(StagingDir, "modules");
        if (Directory.Exists(modules))
            foreach (var file in Directory.GetFiles(modules))
                staged.Add((file, Path.Combine(OutDir, "modules", Path.GetFileName(file))));

        foreach (var (source, destination) in staged)
        {
            Retry(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, overwrite: true);
            }, $"replace {Path.GetRelativePath(OutDir, destination)}");
        }
    }

    /// <summary>
    /// On Windows a directory cannot be renamed while any process holds a file inside it open, and a file
    /// cannot be replaced while it is open without delete sharing. Whoever it is usually lets go within a
    /// second, so retry a few times before giving up.
    /// </summary>
    private void Retry(Action action, string what)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= _retryDelays.Count)
                    throw new SyncPublishException(
                        $"could not {what}: {ex.Message} (another program may have a file in it open.)");
                Thread.Sleep(_retryDelays[attempt]);
            }
        }
    }

    private static void TryDelete(string directory, string what)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  note: could not remove {what} at {directory} ({ex.Message}); it is safe to delete.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(StagingDir)) TryDelete(StagingDir, "the staging directory");
        _lock.Dispose();
    }
}
