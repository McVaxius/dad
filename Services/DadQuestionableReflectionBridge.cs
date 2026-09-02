using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace dad.Services;

public sealed class DadQuestionableReflectionBridgeStatus
{
    public bool QuestionableLoaded { get; set; }
    public bool QuestionableRunning { get; set; }
    public bool Patched { get; set; }
    public bool Pending { get; set; }
    public bool CosmeticPatched { get; set; }
    public bool? DutyGateEnabled { get; set; }
    public string PatchState { get; set; } = "Questionable not loaded.";
    public string CosmeticPatchState { get; set; } = "Questionable not loaded.";
    public string QuestionableVersion { get; set; } = string.Empty;
    public string LastBlocker { get; set; } = string.Empty;
    public string CosmeticLastBlocker { get; set; } = string.Empty;
    public DateTime? LastProbeUtc { get; set; }

    public DadQuestionableReflectionBridgeStatus Clone()
        => new()
        {
            QuestionableLoaded = QuestionableLoaded,
            QuestionableRunning = QuestionableRunning,
            Patched = Patched,
            Pending = Pending,
            CosmeticPatched = CosmeticPatched,
            DutyGateEnabled = DutyGateEnabled,
            PatchState = PatchState,
            CosmeticPatchState = CosmeticPatchState,
            QuestionableVersion = QuestionableVersion,
            LastBlocker = LastBlocker,
            CosmeticLastBlocker = CosmeticLastBlocker,
            LastProbeUtc = LastProbeUtc,
        };
}

public sealed class DadQuestionableReflectionBridge : IDisposable
{
    private const string QuestionableInternalName = "Questionable";
    private const string QuestionablePluginTypeName = "Questionable.QuestionablePlugin";
    private const string AutoDutyIpcTypeName = "Questionable.External.AutoDutyIpc";
    private const string ConfigurationTypeName = "Questionable.Configuration";
    private const string PluginConfigComponentTypeName = "Questionable.Windows.ConfigComponents.PluginConfigComponent";
    private const string PluginInfoTypeName = "Questionable.Windows.ConfigComponents.PluginConfigComponent+PluginInfo";
    private const string PluginProviderTypeName =
        "Questionable.Windows.ConfigComponents.PluginConfigComponent+PluginProvider";
    private const string PluginRequirementTypeName =
        "Questionable.Windows.ConfigComponents.PluginConfigComponent+PluginRequirement";
    private const string PluginDetailInfoTypeName = "Questionable.Windows.ConfigComponents.PluginConfigComponent+PluginDetailInfo";
    private const string AutoDutyInternalName = "AutoDuty";
    private const string DadBridgeDisplayName = "Dad duty bridge";
    private const string DadBridgeDetails = "Dad routes Questionable duties through Dad duty IPC";
    private const string DadRepositoryUrl = "https://github.com/McVaxius/dad";
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags InstanceProperties = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags InstanceConstructors = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly DadDutyIpcService dutyIpcService;
    private readonly DadQuestionableReflectionBridgeStatus status = new();
    private readonly Action<string> runtimeIncompatibilityWarning;
    private volatile bool probeRequested = true;
    private DateTime nextProbeUtc = DateTime.MinValue;
    private string lastLoggedBlocker = string.Empty;
    private string lastLoggedCosmeticBlocker = string.Empty;
    private PatchOwnership? ownership;
    private CosmeticPatchOwnership? cosmeticOwnership;
    private readonly DadQuestionableRuntimeWarningGate runtimeWarningGate = new();

    private sealed class SubscriberTarget
    {
        public required FieldInfo Field { get; init; }
        public required object CurrentValue { get; init; }
        public required object Replacement { get; init; }
    }

    private sealed class PreparedPatch
    {
        public required object QuestionableInstance { get; init; }
        public required object AutoDutyIpc { get; init; }
        public required object Duties { get; init; }
        public required PropertyInfo DutyGateProperty { get; init; }
        public required bool CurrentDutyGateValue { get; init; }
        public required string Version { get; init; }
        public required IReadOnlyList<SubscriberTarget> Subscribers { get; init; }
    }

    private sealed class SubscriberOwnership
    {
        public required FieldInfo Field { get; init; }
        public required object Original { get; init; }
        public required object Replacement { get; init; }
    }

    private sealed class PatchOwnership
    {
        public required object QuestionableInstance { get; init; }
        public required object AutoDutyIpc { get; init; }
        public required object Duties { get; init; }
        public required PropertyInfo DutyGateProperty { get; init; }
        public required bool PreviousDutyGateValue { get; init; }
        public required string Version { get; init; }
        public required List<SubscriberOwnership> Subscribers { get; init; }
        public required int ExpectedSubscriberCount { get; init; }
        public bool DutyGateOwned { get; set; }
    }

    private sealed class PreparedCosmeticPatch
    {
        public required object QuestionableInstance { get; init; }
        public required object PluginConfigComponent { get; init; }
        public required FieldInfo RecommendedPluginsField { get; init; }
        public required object OriginalList { get; init; }
        public required object ReplacementList { get; init; }
        public required object ReplacementEntry { get; init; }
        public required int ReplacementIndex { get; init; }
        public required IReadOnlyList<object> ExpectedEntries { get; init; }
    }

    private sealed class CosmeticPatchOwnership
    {
        public required object QuestionableInstance { get; init; }
        public required object PluginConfigComponent { get; init; }
        public required FieldInfo RecommendedPluginsField { get; init; }
        public required object OriginalList { get; init; }
        public required object ReplacementList { get; init; }
        public required object ReplacementEntry { get; init; }
        public required int ReplacementIndex { get; init; }
        public required IReadOnlyList<object> ExpectedEntries { get; init; }
    }

    private readonly Func<bool> isEnabled;

    public DadQuestionableReflectionBridge(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        DadDutyIpcService dutyIpcService,
        IPluginLog log,
        Func<bool> isEnabled,
        Action<string>? runtimeIncompatibilityWarning = null)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.dutyIpcService = dutyIpcService;
        this.log = log;
        this.isEnabled = isEnabled;
        this.runtimeIncompatibilityWarning = runtimeIncompatibilityWarning ?? (_ => { });

        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        framework.Update += OnFrameworkUpdate;
    }

    public DadQuestionableReflectionBridgeStatus GetStatus()
        => status.Clone();

    public void ResetCharacterLoadWarning()
        => runtimeWarningGate.Reset();

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        for (var attempt = 0; attempt < 3 && cosmeticOwnership != null; attempt++)
            RestoreOwnedCosmeticValue();
        for (var attempt = 0; attempt < 3 && ownership != null; attempt++)
            RestoreOwnedValues();
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (args.AffectedInternalNames.Any(static name =>
                string.Equals(name, QuestionableInternalName, StringComparison.OrdinalIgnoreCase)))
        {
            probeRequested = true;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (!probeRequested && now < nextProbeUtc)
            return;

        probeRequested = false;
        nextProbeUtc = now + ProbeInterval;
        MaintainBridge();
    }

    private void MaintainBridge()
    {
        status.LastProbeUtc = DateTime.UtcNow;

        // Review M19: operator opt-out — restore any owned patches and stop maintaining the bridge.
        if (!isEnabled())
        {
            RestoreOwnedCosmeticValue();
            RestoreOwnedValues();
            status.Patched = false;
            status.Pending = false;
            status.CosmeticPatched = false;
            status.PatchState = "Disabled by operator (QuestionableBridgeEnabled = false).";
            return;
        }

        MaintainRuntimeBridge();
        MaintainCosmeticPatch();
    }

    private unsafe void MaintainRuntimeBridge()
    {
        try
        {
            var exposed = FindLoadedQuestionable();
            if (exposed == null)
            {
                ownership = null;
                status.QuestionableLoaded = false;
                status.QuestionableRunning = false;
                status.Patched = false;
                status.Pending = false;
                status.DutyGateEnabled = null;
                status.QuestionableVersion = string.Empty;
                status.PatchState = "Questionable not loaded.";
                status.LastBlocker = string.Empty;
                lastLoggedBlocker = string.Empty;
                return;
            }

            status.QuestionableLoaded = true;
            status.QuestionableVersion = exposed.Version?.ToString() ?? "unknown";

            var dutyIpcStatus = dutyIpcService.GetStatus();
            if (!dutyIpcStatus.Registered)
            {
                throw Incompatible(
                    $"Dad duty IPC is not registered: {dutyIpcStatus.RegistrationState} {dutyIpcStatus.LastFailure}".Trim());
            }

            var questionableInstance = ResolveQuestionableInstance(exposed);
            if (ownership != null && !ReferenceEquals(ownership.QuestionableInstance, questionableInstance))
                ownership = null;

            var running = QueryQuestionableIsRunning();
            status.QuestionableRunning = running;
            if (running)
            {
                var fateManager = FateManager.Instance();
                if (fateManager != null &&
                    fateManager->CurrentFate != null &&
                    fateManager->IsSyncedToFate(fateManager->CurrentFate))
                {
                    fateManager->LevelSync();
                }
            }

            if (ownership != null && IsFullyOwned(ownership))
            {
                status.Patched = true;
                status.Pending = false;
                status.DutyGateEnabled = true;
                status.QuestionableVersion = ownership.Version;
                status.PatchState = "Patched.";
                status.LastBlocker = string.Empty;
                lastLoggedBlocker = string.Empty;
                return;
            }

            if (ownership != null)
            {
                // Never wait while Questionable is half-patched. Restore each value whose exact
                // replacement is still owned, retaining only failed restores for the next probe.
                RestoreOwnedValues();
                if (ownership != null)
                {
                    status.Patched = false;
                    status.Pending = true;
                    status.PatchState = "Restoring an incomplete owned patch before retry.";
                    status.LastBlocker = "Questionable patch rollback remains incomplete.";
                    return;
                }
            }

            if (running)
            {
                status.Patched = false;
                status.Pending = true;
                status.DutyGateEnabled = TryReadOwnedGate();
                status.PatchState = "Pending until Questionable is idle.";
                status.LastBlocker = "Questionable.IsRunning returned true.";
                lastLoggedBlocker = string.Empty;
                return;
            }

            var prepared = PreparePatch(exposed, questionableInstance);
            ApplyPatch(prepared);
            status.Patched = true;
            status.Pending = false;
            status.DutyGateEnabled = true;
            status.QuestionableVersion = prepared.Version;
            status.PatchState = "Patched.";
            status.LastBlocker = string.Empty;
            lastLoggedBlocker = string.Empty;
        }
        catch (Exception ex)
        {
            status.Patched = false;
            status.Pending = false;
            status.DutyGateEnabled = TryReadOwnedGate();
            status.PatchState = "Blocked by reflection incompatibility.";
            status.LastBlocker = FormatException(ex);
            if (runtimeWarningGate.TryConsume())
            {
                runtimeIncompatibilityWarning(
                    "DAD could not connect its duty bridge to this Questionable version. Duty routing through DAD is unavailable; see DAD status for details.");
            }
            if (!string.Equals(lastLoggedBlocker, status.LastBlocker, StringComparison.Ordinal))
            {
                lastLoggedBlocker = status.LastBlocker;
                log.Warning(ex, "[dad][QuestionableBridge] Bridge maintenance failed: {Blocker}", status.LastBlocker);
            }
        }
    }

    private void MaintainCosmeticPatch()
    {
        try
        {
            var exposed = FindLoadedQuestionable();
            if (exposed == null)
            {
                cosmeticOwnership = null;
                status.CosmeticPatched = false;
                status.CosmeticPatchState = "Questionable not loaded.";
                status.CosmeticLastBlocker = string.Empty;
                lastLoggedCosmeticBlocker = string.Empty;
                return;
            }

            var questionableInstance = ResolveQuestionableInstance(exposed);
            if (cosmeticOwnership != null &&
                !ReferenceEquals(cosmeticOwnership.QuestionableInstance, questionableInstance))
            {
                cosmeticOwnership = null;
            }

            if (cosmeticOwnership != null && IsCosmeticFullyOwned(cosmeticOwnership))
            {
                status.CosmeticPatched = true;
                status.CosmeticPatchState = "Patched.";
                status.CosmeticLastBlocker = string.Empty;
                lastLoggedCosmeticBlocker = string.Empty;
                return;
            }

            if (cosmeticOwnership != null)
            {
                RestoreOwnedCosmeticValue();
                if (cosmeticOwnership != null)
                {
                    status.CosmeticPatched = false;
                    status.CosmeticPatchState = "Restoring an incomplete owned cosmetic patch before retry.";
                    return;
                }
            }

            var prepared = PrepareCosmeticPatch(questionableInstance);
            ApplyCosmeticPatch(prepared);
            status.CosmeticPatched = true;
            status.CosmeticPatchState = "Patched.";
            status.CosmeticLastBlocker = string.Empty;
            lastLoggedCosmeticBlocker = string.Empty;
        }
        catch (Exception ex)
        {
            status.CosmeticPatched = false;
            status.CosmeticPatchState = "Blocked by reflection incompatibility.";
            status.CosmeticLastBlocker = FormatException(ex);
            if (!string.Equals(lastLoggedCosmeticBlocker, status.CosmeticLastBlocker, StringComparison.Ordinal))
            {
                lastLoggedCosmeticBlocker = status.CosmeticLastBlocker;
                log.Warning(
                    ex,
                    "[dad][QuestionableBridge] Cosmetic row maintenance failed without affecting runtime routing: {Blocker}",
                    status.CosmeticLastBlocker);
            }
        }
    }

    private IExposedPlugin? FindLoadedQuestionable()
        => pluginInterface.InstalledPlugins.FirstOrDefault(static plugin =>
            plugin.IsLoaded &&
            string.Equals(plugin.InternalName, QuestionableInternalName, StringComparison.OrdinalIgnoreCase));

    private static object ResolveQuestionableInstance(IExposedPlugin exposed)
    {
        var exposedType = exposed.GetType();
        RequireType(exposedType, "Dalamud.Plugin.ExposedPlugin", "InstalledPlugins Questionable wrapper");

        var localPluginField = RequireField(exposedType, "<plugin>P");
        var localPlugin = localPluginField.GetValue(exposed)
            ?? throw Incompatible("Dalamud.Plugin.ExposedPlugin.<plugin>P returned null.");
        var localPluginType = RequireTypeInHierarchy(
            localPlugin.GetType(),
            "Dalamud.Plugin.Internal.Types.LocalPlugin",
            "Questionable LocalPlugin");

        var instanceField = RequireField(localPluginType, "instance");
        var instance = instanceField.GetValue(localPlugin)
            ?? throw Incompatible("Dalamud LocalPlugin.instance returned null for loaded Questionable.");
        RequireType(instance.GetType(), QuestionablePluginTypeName, "Questionable plugin instance");
        return instance;
    }

    private bool QueryQuestionableIsRunning()
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning").InvokeFunc();
        }
        catch (Exception ex)
        {
            throw Incompatible($"Questionable.IsRunning failed: {FormatException(ex)}");
        }
    }

    private PreparedPatch PreparePatch(IExposedPlugin exposed, object questionableInstance)
    {
        var questionableType = questionableInstance.GetType();
        var assembly = questionableType.Assembly;
        var serviceProviderField = RequireField(questionableType, "_serviceProvider");
        var serviceProviderValue = serviceProviderField.GetValue(questionableInstance)
            ?? throw Incompatible("Questionable.QuestionablePlugin._serviceProvider returned null.");
        if (serviceProviderValue is not IServiceProvider serviceProvider)
        {
            throw Incompatible(
                $"Questionable.QuestionablePlugin._serviceProvider expected IServiceProvider, found {serviceProviderValue.GetType().FullName}.");
        }

        var autoDutyType = assembly.GetType(AutoDutyIpcTypeName, throwOnError: false)
            ?? throw Incompatible($"Missing type {AutoDutyIpcTypeName}.");
        var configurationType = assembly.GetType(ConfigurationTypeName, throwOnError: false)
            ?? throw Incompatible($"Missing type {ConfigurationTypeName}.");
        var autoDutyIpc = serviceProvider.GetService(autoDutyType)
            ?? throw Incompatible($"Questionable service provider returned null for {AutoDutyIpcTypeName}.");
        var configuration = serviceProvider.GetService(configurationType)
            ?? throw Incompatible($"Questionable service provider returned null for {ConfigurationTypeName}.");
        RequireType(autoDutyIpc.GetType(), AutoDutyIpcTypeName, "Questionable AutoDuty IPC service");
        RequireType(configuration.GetType(), ConfigurationTypeName, "Questionable configuration service");

        var autoDutyConfigurationField = RequireField(autoDutyType, "_configuration");
        if (autoDutyConfigurationField.FieldType != configurationType)
        {
            throw Incompatible(
                $"{AutoDutyIpcTypeName}._configuration expected {configurationType.FullName}, found {autoDutyConfigurationField.FieldType.FullName}.");
        }

        if (!ReferenceEquals(autoDutyConfigurationField.GetValue(autoDutyIpc), configuration))
            throw Incompatible($"{AutoDutyIpcTypeName}._configuration does not reference live Questionable configuration.");

        var dutiesProperty = RequireProperty(configurationType, "Duties");
        var duties = dutiesProperty.GetValue(configuration)
            ?? throw Incompatible($"{ConfigurationTypeName}.Duties returned null.");
        var dutyGateProperty = RequireProperty(duties.GetType(), "RunInstancedContentWithAutoDuty");
        if (dutyGateProperty.PropertyType != typeof(bool) || !dutyGateProperty.CanRead || !dutyGateProperty.CanWrite)
        {
            throw Incompatible(
                $"{duties.GetType().FullName}.RunInstancedContentWithAutoDuty expected readable/writable bool property.");
        }

        var replacements = new (string FieldName, Type ExpectedType, object Replacement)[]
        {
            ("_contentHasPath", typeof(ICallGateSubscriber<uint, bool>),
                pluginInterface.GetIpcSubscriber<uint, bool>(DadDutyIpcContract.ContentHasPath)),
            ("_getConfig", typeof(ICallGateSubscriber<string, string>),
                pluginInterface.GetIpcSubscriber<string, string>(DadDutyIpcContract.GetConfig)),
            ("_setConfig", typeof(ICallGateSubscriber<string, string, object>),
                pluginInterface.GetIpcSubscriber<string, string, object>(DadDutyIpcContract.SetConfig)),
            ("_run", typeof(ICallGateSubscriber<uint, int, bool, object>),
                pluginInterface.GetIpcSubscriber<uint, int, bool, object>(DadDutyIpcContract.Run)),
            ("_isStopped", typeof(ICallGateSubscriber<bool>),
                pluginInterface.GetIpcSubscriber<bool>(DadDutyIpcContract.IsStopped)),
            ("_stop", typeof(ICallGateSubscriber<object>),
                pluginInterface.GetIpcSubscriber<object>(DadDutyIpcContract.Stop)),
        };

        var subscribers = new List<SubscriberTarget>(replacements.Length);
        foreach (var replacement in replacements)
        {
            var field = RequireField(autoDutyType, replacement.FieldName);
            if (field.FieldType != replacement.ExpectedType)
            {
                throw Incompatible(
                    $"{AutoDutyIpcTypeName}.{replacement.FieldName} expected {replacement.ExpectedType.FullName}, found {field.FieldType.FullName}.");
            }

            var currentValue = field.GetValue(autoDutyIpc)
                ?? throw Incompatible($"{AutoDutyIpcTypeName}.{replacement.FieldName} returned null.");
            if (!replacement.ExpectedType.IsInstanceOfType(currentValue))
            {
                throw Incompatible(
                    $"{AutoDutyIpcTypeName}.{replacement.FieldName} value does not implement {replacement.ExpectedType.FullName}.");
            }

            if (!replacement.ExpectedType.IsInstanceOfType(replacement.Replacement))
            {
                throw Incompatible(
                    $"Dad replacement for {AutoDutyIpcTypeName}.{replacement.FieldName} does not implement {replacement.ExpectedType.FullName}.");
            }

            subscribers.Add(new SubscriberTarget
            {
                Field = field,
                CurrentValue = currentValue,
                Replacement = replacement.Replacement,
            });
        }

        var reflectedVersion = assembly.GetName().Version?.ToString() ?? "unknown";
        var exposedVersion = exposed.Version?.ToString() ?? "unknown";
        return new PreparedPatch
        {
            QuestionableInstance = questionableInstance,
            AutoDutyIpc = autoDutyIpc,
            Duties = duties,
            DutyGateProperty = dutyGateProperty,
            CurrentDutyGateValue = (bool)(dutyGateProperty.GetValue(duties)
                ?? throw Incompatible($"{duties.GetType().FullName}.RunInstancedContentWithAutoDuty returned null.")),
            Version = $"plugin {exposedVersion} / assembly {reflectedVersion}",
            Subscribers = subscribers,
        };
    }

    private PreparedCosmeticPatch PrepareCosmeticPatch(object questionableInstance)
    {
        var assembly = questionableInstance.GetType().Assembly;
        var providerType = assembly.GetType(PluginProviderTypeName, throwOnError: false);
        var requirementType = assembly.GetType(PluginRequirementTypeName, throwOnError: false);
        switch (DadQuestionableCosmeticAdapterSelector.Select(
                    providerType != null,
                    requirementType != null))
        {
            case DadQuestionableCosmeticAdapter.CurrentPluginProviderRequirement:
                return PrepareCurrentCosmeticPatch(questionableInstance, providerType!, requirementType!);
            case DadQuestionableCosmeticAdapter.LegacyPluginInfo:
                return PrepareLegacyCosmeticPatch(questionableInstance);
            default:
                throw Incompatible("Questionable current cosmetic model is only partially available.");
        }
    }

    private PreparedCosmeticPatch PrepareCurrentCosmeticPatch(
        object questionableInstance,
        Type pluginProviderType,
        Type pluginRequirementType)
    {
        var questionableType = questionableInstance.GetType();
        var assembly = questionableType.Assembly;
        var serviceProviderField = RequireField(questionableType, "_serviceProvider");
        var serviceProviderValue = serviceProviderField.GetValue(questionableInstance)
            ?? throw Incompatible("Questionable.QuestionablePlugin._serviceProvider returned null.");
        if (serviceProviderValue is not IServiceProvider serviceProvider)
        {
            throw Incompatible(
                $"Questionable.QuestionablePlugin._serviceProvider expected IServiceProvider, found {serviceProviderValue.GetType().FullName}.");
        }

        var pluginConfigComponentType = assembly.GetType(PluginConfigComponentTypeName, throwOnError: false)
            ?? throw Incompatible($"Missing type {PluginConfigComponentTypeName}.");
        var pluginDetailInfoType = assembly.GetType(PluginDetailInfoTypeName, throwOnError: false)
            ?? throw Incompatible($"Missing type {PluginDetailInfoTypeName}.");
        var pluginConfigComponent = serviceProvider.GetService(pluginConfigComponentType)
            ?? throw Incompatible($"Questionable service provider returned null for {PluginConfigComponentTypeName}.");
        RequireType(pluginConfigComponent.GetType(), PluginConfigComponentTypeName, "Questionable plugin config component");

        var uriListType = typeof(IReadOnlyList<Uri>);
        var providerListType = typeof(IReadOnlyList<>).MakeGenericType(pluginProviderType);
        var detailListType = typeof(List<>).MakeGenericType(pluginDetailInfoType);
        var extraLinkType = typeof((string Label, Uri Uri)?);
        var providerConstructor = RequireUniqueConstructor(
            pluginProviderType,
            typeof(string),
            typeof(string),
            typeof(Uri),
            typeof(Uri),
            uriListType);
        var requirementConstructor = RequireUniqueConstructor(
            pluginRequirementType,
            typeof(string),
            typeof(string),
            providerListType,
            typeof(string),
            detailListType,
            extraLinkType);

        var providerDisplayNameProperty = RequireReadableProperty(pluginProviderType, "DisplayName", typeof(string));
        var providerInternalNameProperty = RequireReadableProperty(pluginProviderType, "InternalName", typeof(string));
        var providerWebsiteProperty = RequireReadableProperty(pluginProviderType, "WebsiteUri", typeof(Uri));
        var providerRepositoryProperty = RequireReadableProperty(pluginProviderType, "DalamudRepositoryUri", typeof(Uri));
        _ = RequireReadableProperty(pluginProviderType, "DalamudRepositoryAliases", uriListType);
        var groupNameProperty = RequireReadableProperty(pluginRequirementType, "GroupName", typeof(string));
        var descriptionProperty = RequireReadableProperty(pluginRequirementType, "Description", typeof(string));
        var providersProperty = RequireReadableProperty(pluginRequirementType, "Providers", providerListType);
        var configCommandProperty = RequireReadableProperty(pluginRequirementType, "ConfigCommand", typeof(string));
        var detailsToCheckProperty = RequireReadableProperty(pluginRequirementType, "DetailsToCheck", detailListType);
        var extraLinkProperty = RequireReadableProperty(pluginRequirementType, "ExtraLink", extraLinkType);

        var expectedListType = typeof(IReadOnlyList<>).MakeGenericType(pluginRequirementType);
        var recommendedPluginsField = RequireField(pluginConfigComponentType, "_recommendedPlugins");
        if (recommendedPluginsField.FieldType != expectedListType)
        {
            throw Incompatible(
                $"{PluginConfigComponentTypeName}._recommendedPlugins expected {expectedListType.FullName}, found {recommendedPluginsField.FieldType.FullName}.");
        }
        var currentList = recommendedPluginsField.GetValue(pluginConfigComponent)
            ?? throw Incompatible($"{PluginConfigComponentTypeName}._recommendedPlugins returned null.");
        if (!expectedListType.IsInstanceOfType(currentList))
        {
            throw Incompatible(
                $"{PluginConfigComponentTypeName}._recommendedPlugins value does not implement {expectedListType.FullName}.");
        }

        var sourceList = currentList;
        if (cosmeticOwnership != null &&
            ReferenceEquals(cosmeticOwnership.QuestionableInstance, questionableInstance) &&
            ReferenceEquals(cosmeticOwnership.PluginConfigComponent, pluginConfigComponent) &&
            ReferenceEquals(currentList, cosmeticOwnership.ReplacementList))
        {
            sourceList = cosmeticOwnership.OriginalList;
        }
        if (sourceList is not System.Collections.IEnumerable sourceEntries)
            throw Incompatible($"{PluginConfigComponentTypeName}._recommendedPlugins value is not enumerable.");

        var entries = sourceEntries.Cast<object>().ToList();
        if (entries.Any(entry => entry == null || !pluginRequirementType.IsInstanceOfType(entry)))
            throw Incompatible($"{PluginConfigComponentTypeName}._recommendedPlugins contains an incompatible requirement.");
        var autoDutyIndexes = entries
            .Select((entry, index) => new
            {
                Index = index,
                Providers = providersProperty.GetValue(entry) as System.Collections.IEnumerable,
            })
            .Where(candidate => candidate.Providers != null && candidate.Providers.Cast<object>().Count(provider =>
                pluginProviderType.IsInstanceOfType(provider) &&
                string.Equals(
                    providerInternalNameProperty.GetValue(provider) as string,
                    AutoDutyInternalName,
                    StringComparison.Ordinal)) == 1)
            .Select(static candidate => candidate.Index)
            .ToList();
        if (autoDutyIndexes.Count != 1)
        {
            throw Incompatible(
                $"{PluginConfigComponentTypeName}._recommendedPlugins expected exactly one {AutoDutyInternalName} requirement, found {autoDutyIndexes.Count}.");
        }

        var dadRepositoryUri = new Uri(DadRepositoryUrl);
        var replacementProvider = providerConstructor.Invoke(
            [DadBridgeDisplayName, PluginInfo.InternalName, dadRepositoryUri, null, null]);
        if (replacementProvider == null || !pluginProviderType.IsInstanceOfType(replacementProvider))
            throw Incompatible($"{PluginProviderTypeName} constructor returned an incompatible value.");
        ValidatePropertyValue(providerDisplayNameProperty, replacementProvider, DadBridgeDisplayName);
        ValidatePropertyValue(providerInternalNameProperty, replacementProvider, PluginInfo.InternalName);
        ValidatePropertyValue(providerWebsiteProperty, replacementProvider, dadRepositoryUri);
        ValidatePropertyValue(providerRepositoryProperty, replacementProvider, null);

        var replacementProviders = Array.CreateInstance(pluginProviderType, 1);
        replacementProviders.SetValue(replacementProvider, 0);
        if (!providerListType.IsInstanceOfType(replacementProviders))
            throw Incompatible($"Dad provider list does not implement {providerListType.FullName}.");
        var replacementEntry = requirementConstructor.Invoke(
            [DadBridgeDisplayName, DadBridgeDetails, replacementProviders, PluginInfo.Command, null, null]);
        if (replacementEntry == null || !pluginRequirementType.IsInstanceOfType(replacementEntry))
            throw Incompatible($"{PluginRequirementTypeName} constructor returned an incompatible value.");
        ValidatePropertyValue(groupNameProperty, replacementEntry, DadBridgeDisplayName);
        ValidatePropertyValue(descriptionProperty, replacementEntry, DadBridgeDetails);
        ValidatePropertyValue(providersProperty, replacementEntry, replacementProviders);
        ValidatePropertyValue(configCommandProperty, replacementEntry, PluginInfo.Command);
        ValidatePropertyValue(detailsToCheckProperty, replacementEntry, null);
        ValidatePropertyValue(extraLinkProperty, replacementEntry, null);

        var replacementIndex = autoDutyIndexes[0];
        var replacementList = Array.CreateInstance(pluginRequirementType, entries.Count);
        var expectedEntries = new List<object>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var expectedEntry = index == replacementIndex ? replacementEntry : entries[index];
            expectedEntries.Add(expectedEntry);
            replacementList.SetValue(expectedEntry, index);
        }
        if (!expectedListType.IsInstanceOfType(replacementList))
            throw Incompatible($"Dad replacement list does not implement {expectedListType.FullName}.");

        return new PreparedCosmeticPatch
        {
            QuestionableInstance = questionableInstance,
            PluginConfigComponent = pluginConfigComponent,
            RecommendedPluginsField = recommendedPluginsField,
            OriginalList = sourceList,
            ReplacementList = replacementList,
            ReplacementEntry = replacementEntry,
            ReplacementIndex = replacementIndex,
            ExpectedEntries = expectedEntries,
        };
    }

    private PreparedCosmeticPatch PrepareLegacyCosmeticPatch(object questionableInstance)
    {
        var questionableType = questionableInstance.GetType();
        var assembly = questionableType.Assembly;
        var serviceProviderField = RequireField(questionableType, "_serviceProvider");
        var serviceProviderValue = serviceProviderField.GetValue(questionableInstance)
            ?? throw Incompatible("Questionable.QuestionablePlugin._serviceProvider returned null.");
        if (serviceProviderValue is not IServiceProvider serviceProvider)
        {
            throw Incompatible(
                $"Questionable.QuestionablePlugin._serviceProvider expected IServiceProvider, found {serviceProviderValue.GetType().FullName}.");
        }

        var pluginConfigComponentType = assembly.GetType(PluginConfigComponentTypeName, throwOnError: false)
            ?? throw Incompatible($"Missing type {PluginConfigComponentTypeName}.");
        var pluginInfoType = assembly.GetType(PluginInfoTypeName, throwOnError: false)
            ?? throw Incompatible($"Missing type {PluginInfoTypeName}.");
        var pluginDetailInfoType = assembly.GetType(PluginDetailInfoTypeName, throwOnError: false)
            ?? throw Incompatible($"Missing type {PluginDetailInfoTypeName}.");
        var pluginConfigComponent = serviceProvider.GetService(pluginConfigComponentType)
            ?? throw Incompatible($"Questionable service provider returned null for {PluginConfigComponentTypeName}.");
        RequireType(pluginConfigComponent.GetType(), PluginConfigComponentTypeName, "Questionable plugin config component");

        var pluginDetailListType = typeof(List<>).MakeGenericType(pluginDetailInfoType);
        var pluginInfoConstructor = RequireUniqueConstructor(
            pluginInfoType,
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(Uri),
            typeof(Uri),
            typeof(string),
            pluginDetailListType);
        var displayNameProperty = RequireReadableProperty(pluginInfoType, "DisplayName", typeof(string));
        var internalNameProperty = RequireReadableProperty(pluginInfoType, "InternalName", typeof(string));
        var detailsProperty = RequireReadableProperty(pluginInfoType, "Details", typeof(string));
        var websiteUriProperty = RequireReadableProperty(pluginInfoType, "WebsiteUri", typeof(Uri));
        var repositoryUriProperty = RequireReadableProperty(pluginInfoType, "DalamudRepositoryUri", typeof(Uri));
        var configCommandProperty = RequireReadableProperty(pluginInfoType, "ConfigCommand", typeof(string));
        var detailsToCheckProperty = RequireReadableProperty(pluginInfoType, "DetailsToCheck", pluginDetailListType);

        var expectedListType = typeof(IReadOnlyList<>).MakeGenericType(pluginInfoType);
        var recommendedPluginsField = RequireField(pluginConfigComponentType, "_recommendedPlugins");
        if (recommendedPluginsField.FieldType != expectedListType)
        {
            throw Incompatible(
                $"{PluginConfigComponentTypeName}._recommendedPlugins expected {expectedListType.FullName}, found {recommendedPluginsField.FieldType.FullName}.");
        }

        var currentList = recommendedPluginsField.GetValue(pluginConfigComponent)
            ?? throw Incompatible($"{PluginConfigComponentTypeName}._recommendedPlugins returned null.");
        if (!expectedListType.IsInstanceOfType(currentList))
        {
            throw Incompatible(
                $"{PluginConfigComponentTypeName}._recommendedPlugins value does not implement {expectedListType.FullName}.");
        }

        var sourceList = currentList;
        if (cosmeticOwnership != null &&
            ReferenceEquals(cosmeticOwnership.QuestionableInstance, questionableInstance) &&
            ReferenceEquals(cosmeticOwnership.PluginConfigComponent, pluginConfigComponent) &&
            ReferenceEquals(currentList, cosmeticOwnership.ReplacementList))
        {
            sourceList = cosmeticOwnership.OriginalList;
        }

        if (!expectedListType.IsInstanceOfType(sourceList))
        {
            throw Incompatible(
                $"{PluginConfigComponentTypeName} cosmetic source list does not implement {expectedListType.FullName}.");
        }

        if (sourceList is not System.Collections.IEnumerable sourceEntries)
            throw Incompatible($"{PluginConfigComponentTypeName}._recommendedPlugins value is not enumerable.");

        var entries = sourceEntries.Cast<object>().ToList();
        for (var index = 0; index < entries.Count; index++)
        {
            if (!pluginInfoType.IsInstanceOfType(entries[index]))
            {
                throw Incompatible(
                    $"{PluginConfigComponentTypeName}._recommendedPlugins[{index}] expected {pluginInfoType.FullName}, found {entries[index]?.GetType().FullName ?? "null"}.");
            }
        }

        var autoDutyIndexes = entries
            .Select((entry, index) => new
            {
                Index = index,
                InternalName = internalNameProperty.GetValue(entry) as string,
            })
            .Where(entry => string.Equals(entry.InternalName, AutoDutyInternalName, StringComparison.Ordinal))
            .Select(static entry => entry.Index)
            .ToList();
        if (autoDutyIndexes.Count != 1)
        {
            throw Incompatible(
                $"{PluginConfigComponentTypeName}._recommendedPlugins expected exactly one {AutoDutyInternalName} entry, found {autoDutyIndexes.Count}.");
        }

        var dadRepositoryUri = new Uri(DadRepositoryUrl);
        var replacementEntry = pluginInfoConstructor.Invoke(
            [
                DadBridgeDisplayName,
                PluginInfo.InternalName,
                DadBridgeDetails,
                dadRepositoryUri,
                null,
                PluginInfo.Command,
                null,
            ]);
        if (replacementEntry == null || !pluginInfoType.IsInstanceOfType(replacementEntry))
            throw Incompatible($"{PluginInfoTypeName} constructor returned an incompatible value.");

        ValidatePropertyValue(displayNameProperty, replacementEntry, DadBridgeDisplayName);
        ValidatePropertyValue(internalNameProperty, replacementEntry, PluginInfo.InternalName);
        ValidatePropertyValue(detailsProperty, replacementEntry, DadBridgeDetails);
        ValidatePropertyValue(websiteUriProperty, replacementEntry, dadRepositoryUri);
        ValidatePropertyValue(repositoryUriProperty, replacementEntry, null);
        ValidatePropertyValue(configCommandProperty, replacementEntry, PluginInfo.Command);
        ValidatePropertyValue(detailsToCheckProperty, replacementEntry, null);

        var autoDutyIndex = autoDutyIndexes[0];
        var replacementList = Array.CreateInstance(pluginInfoType, entries.Count);
        var expectedEntries = new List<object>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var expectedEntry = index == autoDutyIndex ? replacementEntry : entries[index];
            expectedEntries.Add(expectedEntry);
            replacementList.SetValue(expectedEntry, index);
        }

        if (!expectedListType.IsInstanceOfType(replacementList))
        {
            throw Incompatible(
                $"Dad replacement list does not implement {expectedListType.FullName}.");
        }

        for (var index = 0; index < entries.Count; index++)
        {
            if (!ReferenceEquals(replacementList.GetValue(index), expectedEntries[index]))
                throw Incompatible($"Dad replacement list validation failed at index {index}.");
        }

        return new PreparedCosmeticPatch
        {
            QuestionableInstance = questionableInstance,
            PluginConfigComponent = pluginConfigComponent,
            RecommendedPluginsField = recommendedPluginsField,
            OriginalList = sourceList,
            ReplacementList = replacementList,
            ReplacementEntry = replacementEntry,
            ReplacementIndex = autoDutyIndex,
            ExpectedEntries = expectedEntries,
        };
    }

    private void ApplyCosmeticPatch(PreparedCosmeticPatch prepared)
    {
        cosmeticOwnership = new CosmeticPatchOwnership
        {
            QuestionableInstance = prepared.QuestionableInstance,
            PluginConfigComponent = prepared.PluginConfigComponent,
            RecommendedPluginsField = prepared.RecommendedPluginsField,
            OriginalList = prepared.OriginalList,
            ReplacementList = prepared.ReplacementList,
            ReplacementEntry = prepared.ReplacementEntry,
            ReplacementIndex = prepared.ReplacementIndex,
            ExpectedEntries = prepared.ExpectedEntries,
        };

        try
        {
            prepared.RecommendedPluginsField.SetValue(prepared.PluginConfigComponent, prepared.ReplacementList);
        }
        catch (Exception ex)
        {
            RestoreOwnedCosmeticValue();
            var suffix = cosmeticOwnership == null
                ? string.Empty
                : " Rollback remains owned and will retry on the next maintenance pass.";
            throw Incompatible($"Cosmetic patch mutation failed: {FormatException(ex)}.{suffix}");
        }

        log.Information("[dad][QuestionableBridge] Replaced Questionable AutoDuty recommendation with Dad duty bridge.");
    }

    private void ApplyPatch(PreparedPatch prepared)
    {
        var previousOwnership = ownership != null &&
                                ReferenceEquals(ownership.QuestionableInstance, prepared.QuestionableInstance)
            ? ownership
            : null;
        var ownedSubscribers = prepared.Subscribers.Select(subscriber =>
        {
            var prior = previousOwnership?.Subscribers.FirstOrDefault(item =>
                string.Equals(item.Field.Name, subscriber.Field.Name, StringComparison.Ordinal));
            var original = prior != null && ReferenceEquals(subscriber.CurrentValue, prior.Replacement)
                ? prior.Original
                : subscriber.CurrentValue;
            return new SubscriberOwnership
            {
                Field = subscriber.Field,
                Original = original,
                Replacement = subscriber.Replacement,
            };
        }).ToList();
        var newOwnership = new PatchOwnership
        {
            QuestionableInstance = prepared.QuestionableInstance,
            AutoDutyIpc = prepared.AutoDutyIpc,
            Duties = prepared.Duties,
            DutyGateProperty = prepared.DutyGateProperty,
            PreviousDutyGateValue = previousOwnership?.PreviousDutyGateValue ?? prepared.CurrentDutyGateValue,
            Version = prepared.Version,
            Subscribers = ownedSubscribers,
            ExpectedSubscriberCount = ownedSubscribers.Count,
            DutyGateOwned = previousOwnership?.DutyGateOwned == true || !prepared.CurrentDutyGateValue,
        };

        // Establish ownership before the first mutation so every partial success has its exact
        // pre-image retained even if a later reflected write rejects or throws.
        ownership = newOwnership;

        try
        {
            foreach (var subscriber in prepared.Subscribers)
            {
                subscriber.Field.SetValue(prepared.AutoDutyIpc, subscriber.Replacement);
            }

            if (!prepared.CurrentDutyGateValue)
            {
                prepared.DutyGateProperty.SetValue(prepared.Duties, true);
            }
        }
        catch (Exception ex)
        {
            RestoreOwnedValues();
            var suffix = ownership == null
                ? string.Empty
                : " Rollback remains owned and will retry on the next maintenance pass.";
            throw Incompatible($"Patch mutation failed: {FormatException(ex)}.{suffix}");
        }

        log.Information("[dad][QuestionableBridge] Patched Questionable {Version} to Dad duty IPC.", prepared.Version);
    }

    private bool IsFullyOwned(PatchOwnership patch)
    {
        try
        {
            if (!(bool)(patch.DutyGateProperty.GetValue(patch.Duties) ?? false))
                return false;

            return patch.Subscribers.Count == patch.ExpectedSubscriberCount &&
                   patch.Subscribers.All(subscriber =>
                ReferenceEquals(subscriber.Field.GetValue(patch.AutoDutyIpc), subscriber.Replacement));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCosmeticFullyOwned(CosmeticPatchOwnership patch)
    {
        try
        {
            if (!ReferenceEquals(
                    patch.RecommendedPluginsField.GetValue(patch.PluginConfigComponent),
                    patch.ReplacementList))
            {
                return false;
            }

            if (patch.ReplacementList is not Array replacementList ||
                replacementList.Length != patch.ExpectedEntries.Count ||
                patch.ReplacementIndex < 0 ||
                patch.ReplacementIndex >= replacementList.Length ||
                !ReferenceEquals(replacementList.GetValue(patch.ReplacementIndex), patch.ReplacementEntry))
            {
                return false;
            }

            for (var index = 0; index < replacementList.Length; index++)
            {
                if (!ReferenceEquals(replacementList.GetValue(index), patch.ExpectedEntries[index]))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool? TryReadOwnedGate()
    {
        if (ownership == null)
            return null;

        try
        {
            return (bool?)ownership.DutyGateProperty.GetValue(ownership.Duties);
        }
        catch
        {
            return null;
        }
    }

    private void RestoreOwnedValues()
    {
        var patch = ownership;
        if (patch == null)
            return;

        var failures = new List<string>();
        try
        {
            var exposed = FindLoadedQuestionable();
            if (exposed == null || !ReferenceEquals(ResolveQuestionableInstance(exposed), patch.QuestionableInstance))
            {
                ownership = null;
                return;
            }

            foreach (var subscriber in patch.Subscribers.ToList())
            {
                try
                {
                    if (ReferenceEquals(subscriber.Field.GetValue(patch.AutoDutyIpc), subscriber.Replacement))
                        subscriber.Field.SetValue(patch.AutoDutyIpc, subscriber.Original);
                    if (!ReferenceEquals(subscriber.Field.GetValue(patch.AutoDutyIpc), subscriber.Replacement))
                        patch.Subscribers.Remove(subscriber);
                }
                catch (Exception ex)
                {
                    failures.Add($"{subscriber.Field.Name}: {FormatException(ex)}");
                }
            }

            if (patch.DutyGateOwned)
            {
                try
                {
                    var currentGate = patch.DutyGateProperty.GetValue(patch.Duties);
                    if (currentGate is true)
                        patch.DutyGateProperty.SetValue(patch.Duties, patch.PreviousDutyGateValue);
                    if (!Equals(patch.DutyGateProperty.GetValue(patch.Duties), true))
                        patch.DutyGateOwned = false;
                }
                catch (Exception ex)
                {
                    failures.Add($"gate: {FormatException(ex)}");
                }
            }

            if (patch.Subscribers.Count == 0 && !patch.DutyGateOwned)
            {
                ownership = null;
                log.Information("[dad][QuestionableBridge] Restored Questionable subscribers and duty gate.");
            }
        }
        catch (Exception ex)
        {
            failures.Add(FormatException(ex));
        }

        if (failures.Count > 0)
        {
            status.LastBlocker = $"Restore failed: {string.Join(" | ", failures)}";
            log.Warning("[dad][QuestionableBridge] Failed to restore owned Questionable values: {Failures}", status.LastBlocker);
        }
    }

    private void RestoreOwnedCosmeticValue()
    {
        var patch = cosmeticOwnership;
        if (patch == null)
            return;

        try
        {
            var exposed = FindLoadedQuestionable();
            if (exposed == null || !ReferenceEquals(ResolveQuestionableInstance(exposed), patch.QuestionableInstance))
            {
                cosmeticOwnership = null;
                return;
            }

            if (ReferenceEquals(
                    patch.RecommendedPluginsField.GetValue(patch.PluginConfigComponent),
                    patch.ReplacementList))
            {
                patch.RecommendedPluginsField.SetValue(patch.PluginConfigComponent, patch.OriginalList);
                log.Information("[dad][QuestionableBridge] Restored Questionable AutoDuty recommendation.");
            }
            if (!ReferenceEquals(
                    patch.RecommendedPluginsField.GetValue(patch.PluginConfigComponent),
                    patch.ReplacementList))
                cosmeticOwnership = null;
        }
        catch (Exception ex)
        {
            status.CosmeticLastBlocker = $"Restore failed: {FormatException(ex)}";
            log.Warning(ex, "[dad][QuestionableBridge] Failed to restore owned Questionable cosmetic row.");
        }
    }

    private static FieldInfo RequireField(Type type, string name)
        => type.GetField(name, InstanceFields)
           ?? throw Incompatible($"Missing field {type.FullName}.{name}.");

    private static PropertyInfo RequireProperty(Type type, string name)
        => type.GetProperty(name, InstanceProperties)
           ?? throw Incompatible($"Missing property {type.FullName}.{name}.");

    private static PropertyInfo RequireReadableProperty(Type type, string name, Type expectedType)
    {
        var property = RequireProperty(type, name);
        if (!property.CanRead || property.PropertyType != expectedType)
        {
            throw Incompatible(
                $"{type.FullName}.{name} expected readable {expectedType.FullName} property, found {property.PropertyType.FullName}.");
        }

        return property;
    }

    private static ConstructorInfo RequireUniqueConstructor(Type type, params Type[] expectedParameterTypes)
    {
        var matches = type
            .GetConstructors(InstanceConstructors)
            .Where(constructor => constructor
                .GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .SequenceEqual(expectedParameterTypes))
            .ToList();
        if (matches.Count != 1)
        {
            throw Incompatible(
                $"{type.FullName} expected exactly one compatible private constructor, found {matches.Count}.");
        }

        return matches[0];
    }

    private static void ValidatePropertyValue(PropertyInfo property, object instance, object? expected)
    {
        var actual = property.GetValue(instance);
        if (!Equals(actual, expected))
        {
            throw Incompatible(
                $"{property.DeclaringType?.FullName}.{property.Name} constructor value validation failed.");
        }
    }

    private static void RequireType(Type actual, string expectedFullName, string description)
    {
        if (!string.Equals(actual.FullName, expectedFullName, StringComparison.Ordinal))
            throw Incompatible($"{description} expected type {expectedFullName}, found {actual.FullName ?? actual.Name}.");
    }

    private static Type RequireTypeInHierarchy(Type actual, string expectedFullName, string description)
    {
        for (var current = actual; current != null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, expectedFullName, StringComparison.Ordinal))
                return current;
        }

        throw Incompatible(
            $"{description} expected {expectedFullName} in type hierarchy, found {actual.FullName ?? actual.Name}.");
    }

    private static InvalidOperationException Incompatible(string message)
        => new(message);

    private static string FormatException(Exception ex)
        => $"{ex.GetType().Name}: {ex.Message}";
}
