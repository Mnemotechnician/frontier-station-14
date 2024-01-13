using System.Diagnostics.CodeAnalysis;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Roles;
using Content.Shared.StationRecords;
using Content.Shared.StatusIcon;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using System.Linq;
using Content.Server.Forensics;
using Content.Server.Preferences.Managers;
using Content.Server.Shipyard.Systems;
using Content.Server.StationRecords;
using Content.Shared._NF.Access;
using Content.Shared.Preferences;
using Content.Shared.Shipyard.Components;
using static Content.Shared.Access.Components.IdCardConsoleComponent;
using static Content.Shared.Shipyard.Components.ShuttleDeedComponent;

namespace Content.Server.Access.Systems;

[UsedImplicitly]
public sealed class IdCardConsoleSystem : SharedIdCardConsoleSystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly StationRecordsSystem _record = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly UserInterfaceSystem _userInterface = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly AccessSystem _access = default!;
    [Dependency] private readonly IdCardSystem _idCard = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly IServerPreferencesManager _preferences = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<IdCardConsoleComponent, SharedIdCardSystem.WriteToTargetIdMessage>(OnWriteToTargetIdMessage);
        SubscribeLocalEvent<IdCardConsoleComponent, SharedIdCardSystem.WriteToShuttleDeedMessage>(OnWriteToShuttleDeedMessage);

        // one day, maybe bound user interfaces can be shared too.
        SubscribeLocalEvent<IdCardConsoleComponent, ComponentStartup>(UpdateUserInterface);
        SubscribeLocalEvent<IdCardConsoleComponent, EntInsertedIntoContainerMessage>(UpdateUserInterface);
        SubscribeLocalEvent<IdCardConsoleComponent, EntRemovedFromContainerMessage>(UpdateUserInterface);
    }

    private void OnWriteToTargetIdMessage(EntityUid uid, IdCardConsoleComponent component, SharedIdCardSystem.WriteToTargetIdMessage args)
    {
        if (args.Session.AttachedEntity is not { Valid: true } player)
            return;

        TryWriteToTargetId(uid, args.FullName, args.JobTitle, args.AccessList, args.JobPrototype, player, component);

        UpdateUserInterface(uid, component, args);
    }

    private void OnWriteToShuttleDeedMessage(EntityUid uid, IdCardConsoleComponent component,
        SharedIdCardSystem.WriteToShuttleDeedMessage args)
    {
        if (args.Session.AttachedEntity is not { Valid: true } player)
            return;

        TryWriteToShuttleDeed(uid, args.ShuttleName, args.ShuttleSuffix, player, component);

        UpdateUserInterface(uid, component, args);
    }

    private void UpdateUserInterface(EntityUid uid, IdCardConsoleComponent component, EntityEventArgs args)
    {
        if (!component.Initialized)
            return;

        var privilegedIdName = string.Empty;
        string[]? possibleAccess = null;
        if (component.PrivilegedIdSlot.Item is { Valid: true } item)
        {
            privilegedIdName = EntityManager.GetComponent<MetaDataComponent>(item).EntityName;
            possibleAccess = _accessReader.FindAccessTags(item).ToArray();
        }

        IdCardConsoleBoundUserInterfaceState newState;
        // this could be prettier
        if (component.TargetIdSlot.Item is not { Valid: true } targetId)
        {
            newState = new IdCardConsoleBoundUserInterfaceState(
                component.PrivilegedIdSlot.HasItem,
                PrivilegedIdIsAuthorized(uid, component),
                false,
                null,
                null,
                false,
                null,
                null,
                possibleAccess,
                string.Empty,
                privilegedIdName,
                string.Empty,
                false);
        }
        else
        {
            var targetIdComponent = EntityManager.GetComponent<IdCardComponent>(targetId);
            var targetAccessComponent = EntityManager.GetComponent<AccessComponent>(targetId);

            var jobProto = string.Empty;
            if (_station.GetOwningStation(uid) is { } station
                && EntityManager.TryGetComponent<StationRecordKeyStorageComponent>(targetId, out var keyStorage)
                && keyStorage.Key != null
                && _record.TryGetRecord<GeneralStationRecord>(station, keyStorage.Key.Value, out var record))
            {
                jobProto = record.JobPrototype;
            }

            string?[]? shuttleNameParts = null;
            var hasShuttle = false;
            if (EntityManager.TryGetComponent<ShuttleDeedComponent>(targetId, out var comp))
            {
                shuttleNameParts = new[] { comp.ShuttleName, comp.ShuttleNameSuffix };
                hasShuttle = true;
            }

            newState = new IdCardConsoleBoundUserInterfaceState(
                component.PrivilegedIdSlot.HasItem,
                PrivilegedIdIsAuthorized(uid, component),
                true,
                targetIdComponent.FullName,
                targetIdComponent.JobTitle,
                hasShuttle,
                shuttleNameParts,
                targetAccessComponent.Tags.ToArray(),
                possibleAccess,
                jobProto,
                privilegedIdName,
                EntityManager.GetComponent<MetaDataComponent>(targetId).EntityName,
                IsJobRenameOnly(uid));
        }

        _userInterface.TrySetUiState(uid, IdCardConsoleUiKey.Key, newState);
    }

    /// <summary>
    /// Called whenever an access button is pressed, adding or removing that access from the target ID card.
    /// Writes data passed from the UI into the ID stored in <see cref="IdCardConsoleComponent.TargetIdSlot"/>, if present.
    /// </summary>
    private void TryWriteToTargetId(EntityUid uid,
        string newFullName,
        string newJobTitle,
        List<string> newAccessList,
        string newJobProto,
        EntityUid player,
        IdCardConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (component.TargetIdSlot.Item is not { Valid: true } targetId || !PrivilegedIdIsAuthorized(uid, component))
            return;

        // Frontier
        TryComp<ShuttleIdCardConsoleComponent>(uid, out var shuttleId);
        var isFullAccess = !IsJobRenameOnly(uid, shuttleId);

        // Frontier: add a shuttle suffix to the job title if not present
        if (shuttleId is { AddShuttleSuffix: true } && TryComp<ShuttleDeedComponent>(component.PrivilegedIdSlot.Item, out var deed))
        {
            var suffix = $"({deed.ShuttleNameSuffix})";
            if (!newJobTitle.EndsWith(suffix))
                newJobTitle += " " + suffix;
        }

        // Frontier: wrapped with if
        if (isFullAccess)
            _idCard.TryChangeFullName(targetId, newFullName, player: player);
        _idCard.TryChangeJobTitle(targetId, newJobTitle, player: player);

        if (_prototype.TryIndex<JobPrototype>(newJobProto, out var job)
            && _prototype.TryIndex<StatusIconPrototype>(job.Icon, out var jobIcon))
        {
            _idCard.TryChangeJobIcon(targetId, jobIcon, player: player);
        }

        if (!newAccessList.TrueForAll(x => component.AccessLevels.Contains(x)))
        {
            _sawmill.Warning($"User {ToPrettyString(uid)} tried to write unknown access tag.");
            return;
        }

        var oldTags = _access.TryGetTags(targetId) ?? new List<string>();
        oldTags = oldTags.ToList();

        var privilegedId = component.PrivilegedIdSlot.Item;

        // Frontier - instead of early-returning, this block was wrapped into an if condition
        if (!oldTags.SequenceEqual(newAccessList) && isFullAccess)
        {
            // I hate that C# doesn't have an option for this and don't desire to write this out the hard way.
            // var difference = newAccessList.Difference(oldTags);
            var difference = newAccessList.Union(oldTags).Except(newAccessList.Intersect(oldTags)).ToHashSet();
            // NULL SAFETY: PrivilegedIdIsAuthorized checked this earlier.
            var privilegedPerms = _accessReader.FindAccessTags(privilegedId!.Value).ToHashSet();
            if (!difference.IsSubsetOf(privilegedPerms))
            {
                _sawmill.Warning($"User {ToPrettyString(uid)} tried to modify permissions they could not give/take!");
                return;
            }

            var addedTags = newAccessList.Except(oldTags).Select(tag => "+" + tag).ToList();
            var removedTags = oldTags.Except(newAccessList).Select(tag => "-" + tag).ToList();
            _access.TrySetTags(targetId, newAccessList);

            /*TODO: ECS SharedIdCardConsoleComponent and then log on card ejection, together with the save.
            This current implementation is pretty shit as it logs 27 entries (27 lines) if someone decides to give themselves AA*/
            _adminLogger.Add(LogType.Action, LogImpact.Medium,
                $"{ToPrettyString(player):player} has modified {ToPrettyString(targetId):entity} with the following accesses: [{string.Join(", ", addedTags.Union(removedTags))}] [{string.Join(", ", newAccessList)}]");
        }

        UpdateStationRecord(uid, targetId, isFullAccess ? newFullName : null, newJobTitle, job);
    }

    /// <summary>
    /// Called whenever an attempt to change the shuttle deed of the target id is made.
    /// Writes data passed from the ui to the shuttle deed and the grid of shuttle.
    /// </summary>
    private void TryWriteToShuttleDeed(EntityUid uid,
        string newShuttleName,
        string newShuttleSuffix,
        EntityUid player,
        IdCardConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component) || IsJobRenameOnly(uid))
            return;

        if (component.TargetIdSlot.Item is not { Valid: true } targetId || !PrivilegedIdIsAuthorized(uid, component))
            return;

        if (!EntityManager.TryGetComponent<ShuttleDeedComponent>(targetId, out var shuttleDeed))
            return;
        else if (Deleted(shuttleDeed!.ShuttleUid))
        {
            RemComp<ShuttleDeedComponent>(targetId);
            return;
        }

        // Ensure the name is valid and follows the convention
        var name = newShuttleName.Trim();
        // The suffix is ignored as per request
        // var suffix = newShuttleSuffix;
        var suffix = shuttleDeed.ShuttleNameSuffix;

        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength];
        // if (suffix.Length > MaxSuffixLength)
        //     suffix = suffix[..MaxSuffixLength];

        _shipyard.TryRenameShuttle(targetId, shuttleDeed, name, suffix);

        _adminLogger.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(player):player} has changed the shuttle name of {ToPrettyString(shuttleDeed.ShuttleUid):entity} to {ShipyardSystem.GetFullName(shuttleDeed)}");
    }

    /// <summary>
    /// Returns true if there is an ID in <see cref="IdCardConsoleComponent.PrivilegedIdSlot"/> and said ID satisfies the requirements of <see cref="AccessReaderComponent"/>.
    /// </summary>
    /// <remarks>
    /// Other code relies on the fact this returns false if privileged Id is null. Don't break that invariant.
    /// </remarks>
    private bool PrivilegedIdIsAuthorized(EntityUid uid, IdCardConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return true;

        if (!EntityManager.TryGetComponent<AccessReaderComponent>(uid, out var reader))
            return true;

        var privilegedId = component.PrivilegedIdSlot.Item;
        return privilegedId != null && _accessReader.IsAllowed(privilegedId.Value, uid, reader) &&
               (!TryComp<ShuttleIdCardConsoleComponent>(uid, out var comp) // Frontier
                || !comp.RequiresCaptainAccess || IsIdCaptainOnGrid(privilegedId.Value, uid)); // Frontier
    }

    private void UpdateStationRecord(EntityUid uid, EntityUid targetId, string? newFullName, string newJobTitle, JobPrototype? newJobProto) // Frontier: made newFullName nullable
    {
        if (_station.GetOwningStation(uid) is not { } station
            || !EntityManager.TryGetComponent<StationRecordKeyStorageComponent>(targetId, out var keyStorage)
            || keyStorage.Key is not { } key)
        {
            return;
        }

        // Frontier: find and copy the old record if not present
        if (!_record.TryGetRecord<GeneralStationRecord>(station, key, out var record) && !TryMoveRecord(station, targetId, ref record))
            return;

        if (newFullName != null)
            record.Name = newFullName;
        record.JobTitle = newJobTitle;

        if (newJobProto != null)
        {
            record.JobPrototype = newJobProto.ID;
            record.JobIcon = newJobProto.Icon;
        }

        _record.Synchronize(station);
    }

    // Frontier
    private bool IsJobRenameOnly(EntityUid uid, ShuttleIdCardConsoleComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return false;
        return comp.JobChangeOnly;
    }

    /// <summary>
    ///   Returns true if idCardUid is an id card that owns the provided grid in the form of a purchased shuttle.
    /// </summary>
    private bool IsIdCaptainOnGrid(EntityUid idCardUid, EntityUid targetEntity)
    {
        if (!TryComp<ShuttleDeedComponent>(idCardUid, out var shuttleDeed))
            return false;

        var grid = Transform(targetEntity).GridUid;
        if (grid == null)
            return false;

        return shuttleDeed.ShuttleUid == grid;
    }

    // Copied from ShipyardSystem.Consoles and modified - moves a record from any old station to the new one
    private bool TryMoveRecord(EntityUid targetStation, EntityUid targetId, [NotNullWhen(true)] ref GeneralStationRecord? record)
    {
        var stationList = EntityQueryEnumerator<StationRecordsComponent>();
        var recSuccess = false;
        record = null;

        if (TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage)
            && keyStorage.Key != null)
        {
            while (stationList.MoveNext(out var stationUid, out var stationRecComp))
            {
                if (!_record.TryGetRecord<GeneralStationRecord>(stationUid, keyStorage.Key.Value, out var oldRec, stationRecComp))
                    continue;

                _record.RemoveRecord(stationUid, keyStorage.Key.Value);
                _record.CreateGeneralRecord(targetStation, targetId, oldRec.Name, oldRec.Age, oldRec.Species, oldRec.Gender, "Passenger", oldRec.Fingerprint, oldRec.DNA);

                if (_record.TryGetRecord<GeneralStationRecord>(targetStation, keyStorage.Key.Value, out var newRecord))
                {
                    record = newRecord; // should never fail as we add it right above
                    recSuccess = true;
                    break;
                }
            }

            // TODO: impossible if the player doesn't have existing records - the id itself is not tied to a character.
            // if (!recSuccess
            //     && _preferences.GetPreferences(args.Session.UserId).SelectedCharacter is HumanoidCharacterProfile profile)
            // {
            //     TryComp<FingerprintComponent>(player, out var fingerprintComponent);
            //     TryComp<DnaComponent>(player, out var dnaComponent);
            //     _records.CreateGeneralRecord((EntityUid) shuttleStation, targetId, profile.Name, profile.Age, profile.Species, profile.Gender, $"Captain", fingerprintComponent!.Fingerprint, dnaComponent!.DNA);
            // }

            _record.Synchronize(targetStation);
            _record.Synchronize(targetId);
        }

        return recSuccess;
    }
}
