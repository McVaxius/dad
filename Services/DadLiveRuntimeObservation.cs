using dad.Models;

namespace dad.Services;

internal sealed record DadLiveRuntimeObservation(
    bool IsLoggedIn,
    DadAcquiredCharacter? Character,
    bool UnsafeConditionActive,
    DadWorldLocationObservation? CurrentLocation,
    string UnsafeConditionLabel = "in an unsafe world state",
    bool? UnsafeConditionOutsideQueue = null);
