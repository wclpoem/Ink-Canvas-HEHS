using InkCanvas.Core.Settings;
using System.IO.Compression;
using System.Text;

namespace InkCanvas.Features.Classroom;

/// <summary>资源包解包结果：解包后实际落盘的文件绝对路径与 <c>bundle.json</c> 原文。</summary>
public sealed record ClassroomBundleRestoreResult(IReadOnlyList<string> Files, string ManifestJson);

/// <summary>
/// 课堂资源包：把课件、板书等文件连同 <c>bundle.json</c> 描述打包成 zip，并可解包到指定目录。
/// 解包遇到同名文件时追加 <c>-1</c> / <c>-2</c> 后缀，绝不覆盖既有文件；源文件与源目录只读。
/// 打包受 <see cref="FeatureSettings.EnableResourceBundle"/> 控制，解包不受开关影响。
/// </summary>
public static class ClassroomResourceBundle
{
    /// <summary>资源包内的描述文件名称。</summary>
    public const string ManifestEntryName = "bundle.json";

    /// <summary>
    /// 打包资源包。包内先写入 <paramref name="manifestJson"/> 作为 <c>bundle.json</c>，
    /// 再按顺序加入给定文件（保留文件名，重名时追加 <c>-1</c> 后缀）。
    /// 输出目录不存在会自动创建；任何一步失败都会清理本次的半成品临时文件。
    /// </summary>
    public static void Archive(
        IReadOnlyList<string> files,
        string manifestJson,
        string outputZip,
        FeatureSettings settings = null)
    {
        if (IsDisabled(settings))
        {
            throw new InvalidOperationException("课堂资源包已关闭");
        }

        if (files is null)
        {
            throw new ArgumentNullException(nameof(files));
        }

        if (string.IsNullOrWhiteSpace(outputZip))
        {
            throw new ArgumentException("输出资源包路径不能为空。", nameof(outputZip));
        }

        string fullOutput = Path.GetFullPath(outputZip);
        string outputDirectory = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // 先写同目录 .tmp 再整体替换：失败时正式路径要么是旧文件，要么不存在，不会出现半个 zip。
        string tmp = fullOutput + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteTextEntry(zip, ManifestEntryName, manifestJson ?? "");

                foreach ((string file, string entryName) in BuildEntryPlan(files))
                {
                    // 文件不存在时 CreateEntryFromFile 会抛 FileNotFoundException，交由调用方感知。
                    zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }

            File.Move(tmp, fullOutput, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// 预演打包后的 zip 条目名（与 <see cref="Archive"/> 完全一致），供调用方生成
    /// <c>bundle.json</c> 中的 <c>files</c> 列表：既有结构不变，但名称包含同名文件的 <c>-1</c> 后缀。
    /// 无效/空白路径按打包规则跳过。
    /// </summary>
    public static IReadOnlyList<string> PlanEntryNames(IReadOnlyList<string> files)
        => BuildEntryPlan(files).Select(item => item.EntryName).ToList();

    private static List<(string File, string EntryName)> BuildEntryPlan(IReadOnlyList<string> files)
    {
        var plan = new List<(string, string)>();
        if (files is null)
        {
            return plan;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestEntryName };
        foreach (string file in files)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            plan.Add((file, MakeUniqueEntryName(name, used)));
        }

        return plan;
    }

    /// <summary>
    /// 解包资源包到 <paramref name="targetDir"/>。目标目录不存在会自动创建；
    /// 同名文件按 <c>-1</c> / <c>-2</c> 递增后缀改名，既有文件与源资源包均不会被改写。
    /// <para>
    /// 解包先落到目标目录下的临时子目录，全部条目读写成功后才逐个移动到目标目录；
    /// 任一步失败都会删除临时目录并回滚本次已移动的文件，绝不覆盖既有数据。
    /// </para>
    /// 资源包缺少 <c>bundle.json</c> 时抛出 <see cref="InvalidDataException"/>。
    /// </summary>
    public static ClassroomBundleRestoreResult Restore(string zipPath, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            throw new ArgumentException("资源包路径不能为空。", nameof(zipPath));
        }

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new ArgumentException("目标目录不能为空。", nameof(targetDir));
        }

        string fullZip = Path.GetFullPath(zipPath);
        if (!File.Exists(fullZip))
        {
            throw new FileNotFoundException("资源包不存在。", fullZip);
        }

        string root = Path.GetFullPath(targetDir);
        bool rootCreated = !Directory.Exists(root);
        Directory.CreateDirectory(root);

        try
        {
            using var stream = new FileStream(fullZip, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            ZipArchiveEntry manifestEntry = zip.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
            {
                throw new InvalidDataException("课堂资源包缺少 bundle.json。");
            }

            string manifestJson = ReadEntryText(manifestEntry);

            string tempDirectory = Path.Combine(root, ".restore-" + Guid.NewGuid().ToString("N"));
            var moved = new List<string>();
            try
            {
                Directory.CreateDirectory(tempDirectory);
                var staged = new List<(string Path, string FileName)>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 只取文件名，丢弃包内目录段：既保持打包时的扁平结构，也避免 zip 路径穿越。
                    string fileName = entry.Name;
                    if (string.IsNullOrEmpty(fileName))
                    {
                        continue;
                    }

                    // 包内可能包含同名条目（外部工具产出）；临时目录内也先分配唯一名。
                    string tempName = MakeUniqueEntryName(fileName, usedNames);
                    string tempPath = Path.Combine(tempDirectory, tempName);
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                    staged.Add((tempPath, fileName));
                }

                var written = new List<string>();
                foreach ((string tempPath, string fileName) in staged)
                {
                    string destination = MoveUnique(tempPath, root, fileName);
                    moved.Add(destination);
                    written.Add(destination);
                }

                return new ClassroomBundleRestoreResult(written, manifestJson);
            }
            catch
            {
                foreach (string path in moved)
                {
                    TryDelete(path);
                }
                throw;
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
        catch
        {
            // 解包失败时若目标目录是本次新建的，清理空目录；已存在目录保持不动。
            if (rootCreated)
            {
                TryDeleteDirectoryIfEmpty(root);
            }
            throw;
        }
    }

    private static bool IsDisabled(FeatureSettings settings)
        => settings is not null && !settings.EnableResourceBundle;

    private static void WriteTextEntry(ZipArchive zip, string entryName, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string MakeUniqueEntryName(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName))
        {
            return fileName;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 1; index < 1000; index++)
        {
            string candidate = $"{stem}-{index}{extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("资源包内同名文件过多：" + fileName);
    }

    /// <summary>
    /// 从临时目录移动文件到目标目录，命中同名文件或同名目录时尝试下一个后缀。
    /// 使用 <see cref="File.Move(string, string, bool)"/> 的非覆盖语义确保既有数据不被改写。
    /// </summary>
    private static string MoveUnique(string sourcePath, string root, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 0; index < 1000; index++)
        {
            string candidate = index == 0 ? fileName : $"{stem}-{index}{extension}";
            string path = Path.Combine(root, candidate);
            if (File.Exists(path) || Directory.Exists(path))
            {
                continue;
            }

            File.Move(sourcePath, path, overwrite: false);
            return path;
        }

        throw new IOException("无法为解包文件分配唯一名称：" + fileName);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响异常传播；残留目录以 .restore- 前缀隔离，不参与业务。
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // 空目录清理失败不影响异常传播。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响异常传播；残留的 .tmp 不会被当作正式资源包。
        }
    }
}