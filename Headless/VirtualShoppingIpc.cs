#if ADS_SHOPPING_LAB
using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Plugin.Services;
#endif
using dad.Models;
using dad.Services;

namespace dad.Headless;

internal sealed class VirtualShoppingIpc(Action<string> observe)
{
    private DadAdsShopListPresetRequest? request;
    public string Stage { get; set; } = "running";
    public string Fault { get; set; } = "";
    public object Invoke(string endpoint, object?[] values)
    {
        if (endpoint == "ADS.StartShopListPreset" && values is [string json])
        {
            if (request != null && (Fault != "finite" || Stage is not ("completed" or "cancelled")))
                throw new InvalidOperationException("Unexpected repeated shopping dispatch.");
            request = DadIpcJson.Deserialize<DadAdsShopListPresetRequest>(json)!;
            Stage = "running";
            if (request.PresetId is not ("11111111-1111-1111-1111-111111111111" or "44444444-4444-4444-4444-444444444444") || request.CompletedRowIds.Count != 0)
                throw new InvalidOperationException("Unexpected shopping preset or completion evidence.");
            observe($"ipc:shopping-start:{request.OperationId}:{request.PresetId}");
            if (Fault == "finite")
            {
                if (!request.SupportsFiniteOrderProgress)
                    throw new InvalidOperationException("Finite order caller omitted progress support.");
                observe($"ipc:shopping-progress:{request.CreditedQuantities?.GetValueOrDefault("22222222-2222-2222-2222-222222222222") ?? 0}");
            }
            if (Fault == "uncertain") throw new IOException("Synthetic start response lost after acceptance.");
            return DadIpcJson.Serialize(new DadAdsShopListStartResponse { Version = 1, Accepted = true,
                OperationId = request.OperationId, PresetId = request.PresetId, Disposition = "accepted" });
        }
        if (request == null || values is not [string operation] || operation != request.OperationId)
            throw new InvalidOperationException("Unexpected shopping IPC correlation.");
        if (endpoint == "ADS.CancelShopListPreset")
        { observe($"ipc:shopping-cancel:{operation}"); Stage = "cancelled"; return true; }
        if (endpoint != "ADS.GetShopListPresetStatusJson") throw new InvalidOperationException($"Unknown shopping IPC {endpoint}.");
        observe($"ipc:shopping-poll:{operation}");
        var done = Stage is "completed" or "cancelled";
        var finite = Fault == "finite";
        var baseline = request.CreditedQuantities?.GetValueOrDefault("22222222-2222-2222-2222-222222222222") ?? 0;
        var credited = Math.Min(60, baseline + (Stage == "completed" ? 20 : Stage == "cancelled" ? 10 : 0));
        var fulfilled = Stage == "completed" && (!finite || credited >= 60);
        return DadIpcJson.Serialize(new DadAdsShopListStatusResponse
        {
            Version = 1, OperationId = Fault == "correlation" && !done ? "wrong-operation" : operation, PresetId = request.PresetId,
            Running = !done, Done = done, Succeeded = done ? Stage == "completed" : null,
            Disposition = Stage == "completed" ? finite ? fulfilled ? "fulfilled" : "partial" : "succeeded" : Stage, StatusMessage = "Synthetic shopping observation",
            CreditedQuantities = finite ? new() { ["22222222-2222-2222-2222-222222222222"] = credited } : null,
            CompletedNonRepeatableRowIds = fulfilled ? ["22222222-2222-2222-2222-222222222222"] : [],
            Rows = [new() { RowId = "22222222-2222-2222-2222-222222222222", ItemId = 1, ItemName = "Synthetic supply",
                Repeatable = false, RefillToAtLeast = finite ? 60 : 10, Outcome = fulfilled ? "purchased" : "pending",
                OwnedQuantity = Stage == "completed" ? 10 : 0, PurchasedQuantity = Stage == "completed" ? finite ? 20 : 10 : 0 }],
        });
    }
}

#if ADS_SHOPPING_LAB
internal sealed class VirtualAurianaShop : IShopPurchaseRuntime, IShopPurchaseClock, IShopSheetSource
{
    private const uint NpcId = 1008119;
    private const uint TerritoryId = 156;
    private const uint AetheryteId = 24;
    private const uint ShopId = 1769533;
    private const uint ItemId = 8876;
    private const int Price = 345;
    private static readonly Vector3 Approach = new(63.283752f, 31.124842f, -735.31555f);
    private static readonly Vector3 NpcPosition = new(62.3635f, 31.288f, -739.956f);

    private readonly UtilityAutomationService utility;
    private readonly ShopListService shops;
    private readonly VirtualClock clock;
    private readonly bool blockCleanup;
    private readonly Action<string> observe;
    private readonly Guid presetId;
    private readonly Guid rowId;
    private DateTime? teleportAt;
    private DateTime? moveAt;
    private DateTime? settledAt;
    private DateTime? cleanupDeadline;
    private DateTime? shopClosedAt;
    private DateTime? terminalAt;
    private bool returningMenuSeen;
    private bool missingPlayerSeen;
    private bool navigationRunning;
    private int purchaseCount;
    private long inventory;
    private long poetics = 690;

    public VirtualAurianaShop(string directory, VirtualClock clock, ulong characterId, bool blockCleanup, IPluginLog log, Action<string> observe)
    {
        this.clock = clock;
        this.blockCleanup = blockCleanup;
        this.observe = observe;
        CharacterId = characterId;
        var store = new ShopListPresetStore(directory, log);
        if (!store.Create("Auriana order", out var error) ||
            !store.ConfigureActive(ShopListMode.FillOrderOverMultipleRuns, ShopCurrencyKind.Tomestone, 28, 0, out error) ||
            !store.SetItem(ItemId, 1, 1, false, ShopListOwnershipScope.InventoryOnly, out error))
            throw new InvalidOperationException(error);
        presetId = store.ActivePresetId;
        rowId = store.ActivePreset.Items.Single().RowId;
        utility = new UtilityAutomationService(new ShopCatalogService(this), this, this, log);
        shops = new ShopListService(store, null!, utility,
            _ => throw new InvalidOperationException("The inventory-only fixture queried retainers."), log);
    }

    public object Invoke(string endpoint, object?[] values)
    {
        if (values is not [string value]) throw new InvalidOperationException($"Unexpected shopping IPC arguments for {endpoint}.");
        switch (endpoint)
        {
            case "ADS.StartShopListPreset":
                var request = DadIpcJson.Deserialize<DadAdsShopListPresetRequest>(value)!;
                observe($"ipc:shopping-start:{request.OperationId}:{request.PresetId}");
                return shops.StartShopListPresetJson(value);
            case "ADS.GetShopListPresetStatusJson":
                observe($"ipc:shopping-poll:{value}");
                return shops.GetShopListPresetStatusJson(value);
            case "ADS.CancelShopListPreset":
                observe($"ipc:shopping-cancel:{value}");
                return shops.CancelShopListPreset(value);
            default:
                throw new InvalidOperationException($"Unexpected shopping IPC endpoint {endpoint}.");
        }
    }

    public void Update()
    {
        if (CurrentTerritoryId == TerritoryId && !HasLocalPlayer && !missingPlayerSeen)
        {
            missingPlayerSeen = true;
            observe("shop:arrived-without-player");
        }
        if (shopClosedAt is { } closedAt && UtcNow > closedAt && !returningMenuSeen)
        {
            returningMenuSeen = true;
            IsSelectionMenuVisible = true;
            observe("shop:owned-menu-returned");
        }
        utility.Update();
        if (utility.ShopListBatchStatus.Done && terminalAt == null)
        {
            terminalAt = UtcNow;
            observe($"shop:terminal:{utility.ShopListBatchStatus.Disposition}");
        }
    }

    public object Snapshot() => new
    {
        presetId, rowId, purchaseCount, inventory, poetics, settledAt, cleanupDeadline, terminalAt,
        IsBetweenAreas, CurrentTerritoryId, HasLocalPlayer, IsAnyShopVisible, IsSelectionMenuVisible,
        status = utility.ShopListBatchStatus,
    };

    public DateTime UtcNow => clock.Now.UtcDateTime;
    public bool IsLoggedIn => true;
    public ulong CharacterId { get; }
    public bool IsBetweenAreas => teleportAt is { } started && UtcNow < started.AddSeconds(1);
    public bool HasLocalPlayer => teleportAt == null || UtcNow >= teleportAt.Value.AddSeconds(2);
    public bool IsPlayerAvailable => HasLocalPlayer && !IsBetweenAreas;
    public uint CurrentTerritoryId => teleportAt.HasValue && !IsBetweenAreas ? TerritoryId : 820u;
    public Vector3 PlayerPosition => Approach + new Vector3(moveAt is { } started && UtcNow >= started.AddSeconds(1) ? 0.99f : 1.01f, 0, 0);
    public bool HasVnavmesh => true;
    public bool HasLifestream => true;
    public bool HasUnexpectedConfirmation => false;
    public bool IsSelectionMenuVisible { get; private set; }
    public bool IsAnyShopVisible { get; private set; }
    public string? ShopListCleanupBlocker => IsAnyShopVisible || IsSelectionMenuVisible || !returningMenuSeen
        ? "Auriana's interaction is still active" : null;

    public bool IsAetheryteUnlocked(uint id) => id == AetheryteId;
    public bool IsQuestComplete(uint id) => true;
    public long GetItemCount(uint id) => id == ItemId ? inventory : 0;
    public long GetAvailableCurrency(ShopCurrencyCost currency) => currency.Identity == new ShopCurrencyIdentity(ShopCurrencyKind.Tomestone, 28) ? poetics : 0;
    public long GetInventoryCapacity(uint id, uint stackSize) => id == ItemId ? 1 : 0;
    public bool TryResolveFloor(Vector3 position, out Vector3 floorPosition)
        => throw new InvalidOperationException("The authored approach should not query the floor.");

    public bool TryTeleport(ResolvedShopRoute route)
    {
        if (route.AetheryteId != AetheryteId || route.TerritoryId != TerritoryId || teleportAt.HasValue)
            throw new InvalidOperationException("Unexpected Auriana teleport.");
        teleportAt = UtcNow;
        observe($"shop:teleport:{route.AetheryteId}");
        return true;
    }

    public bool TryMove(Vector3 destination, string label)
    {
        if (destination != Approach || !IsPlayerAvailable || CurrentTerritoryId != TerritoryId || moveAt.HasValue)
            throw new InvalidOperationException("Navigation must use the exact Auriana approach after the player's arrival.");
        moveAt = UtcNow;
        navigationRunning = true;
        observe("shop:move-to-approach");
        return true;
    }

    public bool TryGetNavigationRunning(out bool running) { running = navigationRunning; return true; }

    public ShopNavigationStopResult TryStopNavigation()
    {
        if (navigationRunning)
        {
            if (Vector3.Distance(PlayerPosition, Approach) > 1f) throw new InvalidOperationException("Navigation stopped outside Auriana's approach gate.");
            navigationRunning = false;
            observe("shop:stop-at-approach");
        }
        return ShopNavigationStopResult.Stopped;
    }

    public bool TryGetNpc(uint id, out ShopRuntimeNpc npc)
    {
        npc = new(NpcPosition, Vector3.Distance(NpcPosition, PlayerPosition));
        return id == NpcId && CurrentTerritoryId == TerritoryId && IsPlayerAvailable;
    }

    public bool TryInteractNpc(uint id)
    {
        if (!TryGetNpc(id, out _) || navigationRunning || Vector3.Distance(PlayerPosition, Approach) > 1f)
            throw new InvalidOperationException("Auriana interaction must follow navigation and the one-yalm approach gate.");
        IsSelectionMenuVisible = true;
        observe($"shop:interact:{id}");
        return true;
    }

    public bool TrySelectMenu(int index)
    {
        if (!IsSelectionMenuVisible || index != 0) throw new InvalidOperationException("Unexpected Auriana menu selection.");
        IsSelectionMenuVisible = false;
        IsAnyShopVisible = true;
        return true;
    }

    public bool IsExpectedShopVisible(ShopOfferKind kind) => kind == ShopOfferKind.SpecialShopTomestone && IsAnyShopVisible;
    public ShopUiValidationResult ValidateShopUi(EvaluatedShopOffer offer) => ShopUiValidationResult.Valid(0);

    public bool SubmitPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount)
    {
        if (offer.Offer.ShopId != ShopId || !IsAnyShopVisible || runtimeRow != 0 || transactionCount != 1 || purchaseCount != 0)
            throw new InvalidOperationException("Expected one purchase from Auriana's Poetics shop.");
        purchaseCount++;
        inventory++;
        poetics -= Price;
        observe($"shop:purchase:{ItemId}:{Price}");
        return true;
    }

    public void CloseOwnedShopUi() { }

    public void ContinueShopListCleanup(DateTime deadlineUtc, bool attemptClose)
    {
        if (cleanupDeadline == null)
        {
            cleanupDeadline = deadlineUtc;
            settledAt = UtcNow;
        }
        if (deadlineUtc != cleanupDeadline || (UtcNow >= deadlineUtc && attemptClose))
            throw new InvalidOperationException("ADS changed the cleanup deadline or attempted to close at expiry.");
        if (!attemptClose) return;
        if (IsAnyShopVisible && UtcNow >= settledAt!.Value.AddSeconds(5))
        {
            IsAnyShopVisible = false;
            shopClosedAt = UtcNow;
            observe("shop:owned-shop-closed");
        }
        if (IsSelectionMenuVisible && returningMenuSeen && !blockCleanup && UtcNow >= settledAt!.Value.AddSeconds(6))
        {
            IsSelectionMenuVisible = false;
            observe("shop:owned-menu-closed");
        }
    }

    public ShopCatalogSnapshot BuildSnapshot()
    {
        var shops = new HashSet<uint> { ShopId, 1769781 };
        var links = ShopNpcLinkBuilder.Build(new HashSet<uint>(), shops,
            [new(NpcId, "Auriana", [new(ShopNpcEventKind.SpecialShop, ShopId),
                new(ShopNpcEventKind.Unsupported, 0), new(ShopNpcEventKind.SpecialShop, 1769781)])], [], []);
        return new(new Dictionary<uint, ShopItemSheetRow>
            {
                [ItemId] = new(ItemId, "Augmented Ironworks Helm of Fending", 1, 0, true),
                [28] = new(28, "Allagan Tomestones of Poetics", 2000, 0, false),
            }, [],
            [new(ShopId, "Allagan Tomestones of Poetics (DoW)", 0, [(ItemId, 1u, false)], [new(1, Price, 0, 2)], 4, [], false)],
            links, [new(NpcId, TerritoryId, "Mor Dhona", NpcPosition, 2)],
            [new(AetheryteId, "Revenant's Toll", TerritoryId, Vector3.Zero)],
            [new(1, 28, "Allagan Tomestones of Poetics")]);
    }
}
#endif
