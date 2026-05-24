using System.IO;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class MemoryManager
{
    private readonly string _workspacePath;
    private readonly LogService _log;

    public string WorkspacePath => _workspacePath;
    public string ConfigPath => Path.Combine(_workspacePath, "config");
    public string RuntimeConfigPath => Path.Combine(_workspacePath, "config", "runtime");
    public string SeedsPath => Path.Combine(_workspacePath, "config", "seeds");
    public string MemoryPath => Path.Combine(_workspacePath, "memory");
    public string ConversationsPath => Path.Combine(MemoryPath, "conversations");
    public string TopicsPath => Path.Combine(MemoryPath, "topics");
    public string MetaPath => Path.Combine(MemoryPath, "meta");
    public string BackupPath => Path.Combine(MemoryPath, "backup");
    public string ProjectsPath => Path.Combine(MemoryPath, "projects");
    public string DreamingPath => Path.Combine(MemoryPath, "dreaming");

    public MemoryManager(string workspacePath, LogService log)
    {
        _workspacePath = workspacePath;
        _log = log;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RuntimeConfigPath);
        Directory.CreateDirectory(SeedsPath);
        Directory.CreateDirectory(ConversationsPath);
        Directory.CreateDirectory(TopicsPath);
        Directory.CreateDirectory(MetaPath);
        Directory.CreateDirectory(BackupPath);
        Directory.CreateDirectory(ProjectsPath);
        Directory.CreateDirectory(DreamingPath);

        EnsureSeedsPopulated();
        EnsureRuntimeConfig();

        _log.Info("memory", $"Workspace ready at {_workspacePath}");
    }

    public string ReadIdentity()
    {
        var runtimeIdentity = Path.Combine(RuntimeConfigPath, "identity.md");
        if (File.Exists(runtimeIdentity))
            return File.ReadAllText(runtimeIdentity);

        var seedIdentity = Path.Combine(SeedsPath, "identity.md");
        return File.Exists(seedIdentity) ? File.ReadAllText(seedIdentity) : "";
    }

    public void WriteIdentity(string content)
    {
        var path = Path.Combine(RuntimeConfigPath, "identity.md");
        File.WriteAllText(path, content);
        _log.Info("memory", "Identity updated");
    }

    public string ReadOutputProtocol()
    {
        var path = Path.Combine(ConfigPath, "output_protocol.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public string ReadCapabilities()
    {
        var path = Path.Combine(ConfigPath, "capabilities.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public string[] GetMemoryManifest()
    {
        if (!Directory.Exists(MemoryPath)) return [];

        return Directory.GetFiles(MemoryPath, "*.md", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(BackupPath, StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(_workspacePath, p).Replace('\\', '/'))
            .OrderBy(p => p)
            .ToArray();
    }

    public string? ReadMemoryFile(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, relativePath));
        if (!fullPath.StartsWith(_workspacePath, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    public void WriteMemoryFile(string relativePath, string content)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, relativePath));
        if (!fullPath.StartsWith(_workspacePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes workspace");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public async Task BackupAsync()
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var backupDir = Path.Combine(BackupPath, today);

        if (Directory.Exists(backupDir))
            return;

        Directory.CreateDirectory(backupDir);
        await Task.Run(() => CopyDirectory(MemoryPath, backupDir, excludeBackup: true));

        PruneOldBackups(maxDays: 7);
        _log.Info("memory", $"Backup created: {today}");
    }

    public void GenerateClaudeMd()
    {
        var identity = ReadIdentity();
        var protocol = ReadOutputProtocol();
        var capabilities = ReadCapabilities();
        var manifest = GetMemoryManifest();

        var content = $"""
            {identity}

            ---

            # Output Protocol
            {protocol}

            ---

            # Capabilities
            {capabilities}

            ---

            # Memory
            You have a file-based memory system in `memory/`.
            This is YOUR memory. You own it. Create files, add folders, reorganize as you see fit.

            When you learn something worth remembering across conversations, write it down.
            Don't wait for dreaming to do it. Dreaming consolidates, but you should capture in real-time.

            Read `memory/index.md` for structure and details. Current files:
            {string.Join("\n", manifest.Select(p => $"- {p}"))}
            """;

        var path = Path.Combine(_workspacePath, "CLAUDE.md");
        File.WriteAllText(path, content);
        _log.Info("memory", "CLAUDE.md generated");
    }

    public string ReadHeartbeats()
    {
        var path = Path.Combine(RuntimeConfigPath, "heartbeats.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void WriteHeartbeats(string content)
    {
        File.WriteAllText(Path.Combine(RuntimeConfigPath, "heartbeats.md"), content);
    }

    private void EnsureSeedsPopulated()
    {
        var seedIdentity = Path.Combine(SeedsPath, "identity.md");
        if (!File.Exists(seedIdentity))
        {
            var repoSeed = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "nova-workspace", "config", "seeds", "identity.md");
            repoSeed = Path.GetFullPath(repoSeed);
            if (File.Exists(repoSeed))
                File.Copy(repoSeed, seedIdentity);
        }
    }

    private void EnsureRuntimeConfig()
    {
        var runtimeIdentity = Path.Combine(RuntimeConfigPath, "identity.md");
        if (!File.Exists(runtimeIdentity))
        {
            var seed = Path.Combine(SeedsPath, "identity.md");
            if (File.Exists(seed))
                File.Copy(seed, runtimeIdentity);
        }
    }

    private static void CollectMarkdownFiles(string dir, List<string> paths)
    {
        if (!Directory.Exists(dir)) return;
        paths.AddRange(Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories));
    }

    private static void CopyDirectory(string source, string dest, bool excludeBackup)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            if (excludeBackup && dirName == "backup") continue;
            var destSub = Path.Combine(dest, dirName);
            Directory.CreateDirectory(destSub);
            CopyDirectory(dir, destSub, excludeBackup: false);
        }
    }

    private void PruneOldBackups(int maxDays)
    {
        if (!Directory.Exists(BackupPath)) return;
        var cutoff = DateTime.UtcNow.AddDays(-maxDays);
        foreach (var dir in Directory.GetDirectories(BackupPath))
        {
            var name = Path.GetFileName(dir);
            if (DateTime.TryParseExact(name, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date) && date < cutoff)
            {
                Directory.Delete(dir, recursive: true);
                _log.Info("memory", $"Pruned old backup: {name}");
            }
        }
    }
}
