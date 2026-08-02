namespace dad.Services;

internal sealed class DadAtomicFileStore
{
    private readonly Action<string, string> writeAllText;
    private readonly Func<string, bool> exists;
    private readonly Action<string, string> replace;
    private readonly Action<string, string> move;
    private readonly Action<string> delete;
    private readonly Func<string> uniqueSuffix;

    public DadAtomicFileStore()
        : this(
            File.WriteAllText,
            File.Exists,
            (source, destination) => File.Replace(source, destination, null),
            File.Move,
            File.Delete,
            () => Guid.NewGuid().ToString("N"))
    {
    }

    internal DadAtomicFileStore(
        Action<string, string> writeAllText,
        Func<string, bool> exists,
        Action<string, string> replace,
        Action<string, string> move,
        Action<string> delete,
        Func<string> uniqueSuffix)
    {
        this.writeAllText = writeAllText;
        this.exists = exists;
        this.replace = replace;
        this.move = move;
        this.delete = delete;
        this.uniqueSuffix = uniqueSuffix;
    }

    public void Write(string destinationPath, string contents)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("Atomic destination must have a parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{uniqueSuffix()}.tmp");

        try
        {
            writeAllText(temporaryPath, contents);
            if (exists(destinationPath))
                replace(temporaryPath, destinationPath);
            else
                move(temporaryPath, destinationPath);
        }
        finally
        {
            try
            {
                if (exists(temporaryPath))
                    delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup must not hide the original persistence failure.
            }
        }
    }
}
