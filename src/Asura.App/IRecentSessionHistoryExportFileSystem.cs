namespace Asura.App;

public interface IRecentSessionHistoryExportFileSystem
{
    void CreateDirectory(string path);

    Stream CreateTemporaryFile(string path);

    void Publish(string temporaryPath, string destinationPath);

    void Delete(string path);
}

public sealed class LocalRecentSessionHistoryExportFileSystem
    : IRecentSessionHistoryExportFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream CreateTemporaryFile(string path) => new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    public void Publish(string temporaryPath, string destinationPath) =>
        File.Move(temporaryPath, destinationPath, overwrite: true);

    public void Delete(string path) => File.Delete(path);
}
