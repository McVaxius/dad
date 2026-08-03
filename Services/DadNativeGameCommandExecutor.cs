using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace dad.Services;

internal sealed unsafe class DadNativeGameCommandExecutor : IDadGameCommandExecutor
{
    internal const string UiModuleUnavailableError =
        "The native game UI module is unavailable for chat-command submission.";

    public bool TryExecute(string command, out string error)
    {
        if (!DadNativeChatCommandRules.TryNormalize(command, out var normalized, out error))
            return false;

        Utf8String* entry = null;
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                error = UiModuleUnavailableError;
                return false;
            }

            entry = Utf8String.FromString(normalized);
            if (entry == null)
            {
                error = "The native chat-command entry could not be allocated.";
                return false;
            }

            uiModule->ProcessChatBoxEntry(entry, nint.Zero);
            return true;
        }
        catch (Exception exception)
        {
            error = $"The native chat command failed: {exception.Message}";
            return false;
        }
        finally
        {
            if (entry != null)
                entry->Dtor(true);
        }
    }
}
