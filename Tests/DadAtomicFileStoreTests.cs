using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAtomicFileStoreTests
{
    [Fact]
    public void FirstWriteUsesUniqueSameDirectoryTemporaryAndMove()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? writtenPath = null;
        string? movedFrom = null;
        string? movedTo = null;
        var store = CreateStore(
            files,
            write: (path, _) =>
            {
                writtenPath = path;
                files.Add(path);
            },
            move: (source, destination) =>
            {
                movedFrom = source;
                movedTo = destination;
                files.Remove(source);
                files.Add(destination);
            });

        store.Write(@"C:\config\account_dad.json", "payload");

        Assert.Equal(@"C:\config\.account_dad.json.fixed.tmp", writtenPath);
        Assert.Equal(writtenPath, movedFrom);
        Assert.Equal(@"C:\config\account_dad.json", movedTo);
        Assert.DoesNotContain(writtenPath!, files);
        Assert.Contains(movedTo!, files);
    }

    [Fact]
    public void ExistingWriteUsesAtomicReplace()
    {
        var destination = @"C:\config\account_dad.json";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destination };
        string? replacedFrom = null;
        var store = CreateStore(
            files,
            write: (path, _) => files.Add(path),
            replace: (source, target) =>
            {
                replacedFrom = source;
                Assert.Equal(destination, target);
                files.Remove(source);
            });

        store.Write(destination, "replacement");

        Assert.Equal(@"C:\config\.account_dad.json.fixed.tmp", replacedFrom);
        Assert.Contains(destination, files);
        Assert.DoesNotContain(replacedFrom!, files);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommitFailureDeletesTemporaryWithoutHidingFailure(bool destinationExists)
    {
        var destination = @"C:\config\account_dad.json";
        var files = destinationExists
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destination }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        var store = CreateStore(
            files,
            write: (path, _) => files.Add(path),
            replace: (_, _) => throw new IOException("replace failed"),
            move: (_, _) => throw new IOException("move failed"),
            delete: path =>
            {
                deleted.Add(path);
                files.Remove(path);
            });

        var error = Assert.Throws<IOException>(() => store.Write(destination, "payload"));

        Assert.Equal(destinationExists ? "replace failed" : "move failed", error.Message);
        Assert.Single(deleted);
        Assert.Equal(@"C:\config\.account_dad.json.fixed.tmp", deleted[0]);
        Assert.DoesNotContain(deleted[0], files);
        Assert.Equal(destinationExists, files.Contains(destination));
    }

    [Fact]
    public void CleanupFailureDoesNotReplaceOriginalWriteFailure()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var store = CreateStore(
            files,
            write: (path, _) =>
            {
                files.Add(path);
                throw new IOException("write failed");
            },
            delete: _ => throw new UnauthorizedAccessException("cleanup failed"));

        var error = Assert.Throws<IOException>(() =>
            store.Write(@"C:\config\account_dad.json", "payload"));

        Assert.Equal("write failed", error.Message);
    }

    private static DadAtomicFileStore CreateStore(
        HashSet<string> files,
        Action<string, string>? write = null,
        Action<string, string>? replace = null,
        Action<string, string>? move = null,
        Action<string>? delete = null)
        => new(
            write ?? ((path, _) => files.Add(path)),
            files.Contains,
            replace ?? ((source, _) => files.Remove(source)),
            move ?? ((source, destination) =>
            {
                files.Remove(source);
                files.Add(destination);
            }),
            delete ?? (path => files.Remove(path)),
            () => "fixed");
}
