using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace dad.Services;

internal sealed unsafe class DadNativeGameCommandExecutor : IDadGameCommandExecutor
{
    public bool TryExecute(string command, out string error)
    {
        error = string.Empty;
        if (!string.Equals(
                command,
                DadAlliancePartyFinderCommandDispatcher.PartyFinderCommand,
                StringComparison.Ordinal))
        {
            error = "The native game-command executor accepts only the exact /pfinder command.";
            return false;
        }

        Utf8String* entry = null;
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                error = "The native game UI module is unavailable for /pfinder.";
                return false;
            }

            entry = Utf8String.FromString(command);
            if (entry == null)
            {
                error = "The native /pfinder chat entry could not be allocated.";
                return false;
            }

            uiModule->ProcessChatBoxEntry(entry, nint.Zero);
            return true;
        }
        catch (Exception exception)
        {
            error = $"The native /pfinder command failed: {exception.Message}";
            return false;
        }
        finally
        {
            if (entry != null)
                entry->Dtor(true);
        }
    }
}
