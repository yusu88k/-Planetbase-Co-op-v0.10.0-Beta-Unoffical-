using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace PlanetbaseCoop
{
    public enum CoopDueTickState
    {
        Invalid,
        Future,
        Due,
        Late
    }

    public static class CoopDueTickPolicy
    {
        public static CoopDueTickState Classify(long applyTick, long currentTick)
        {
            if (applyTick <= 0 || currentTick < 0 ||
                applyTick > CoopProtocol.MaxWorldTick ||
                currentTick > CoopProtocol.MaxWorldTick)
                return CoopDueTickState.Invalid;
            if (applyTick > currentTick) return CoopDueTickState.Future;
            return applyTick == currentTick
                ? CoopDueTickState.Due
                : CoopDueTickState.Late;
        }
    }

    public sealed class CoopObjectReference
    {
        public CoopObjectReference(
            int objectId,
            string typeName,
            float positionX,
            float positionY,
            float positionZ,
            bool hasPosition)
        {
            ObjectId = objectId;
            TypeName = typeName ?? string.Empty;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            HasPosition = hasPosition;
        }

        public int ObjectId { get; private set; }
        public string TypeName { get; private set; }
        public float PositionX { get; private set; }
        public float PositionY { get; private set; }
        public float PositionZ { get; private set; }
        public bool HasPosition { get; private set; }

        public CoopObjectReference Clone()
        {
            return new CoopObjectReference(
                ObjectId,
                TypeName,
                PositionX,
                PositionY,
                PositionZ,
                HasPosition);
        }
    }

    public sealed class CoopObjectCandidate
    {
        public CoopObjectCandidate(
            int objectId,
            string typeName,
            float positionX,
            float positionY,
            float positionZ)
        {
            ObjectId = objectId;
            TypeName = typeName ?? string.Empty;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
        }

        public int ObjectId { get; private set; }
        public string TypeName { get; private set; }
        public float PositionX { get; private set; }
        public float PositionY { get; private set; }
        public float PositionZ { get; private set; }
    }

    public enum CoopObjectResolution
    {
        Invalid,
        Missing,
        Ambiguous,
        ExactId,
        UniqueSpatialFallback
    }

    public static class CoopObjectReferenceValidator
    {
        public const int MaxTypeNameChars = 96;
        public const float MaxWorldCoordinate = 2500f;

        public static bool IsValid(CoopObjectReference reference)
        {
            if (reference == null || reference.ObjectId <= 0 ||
                !IsSafeTypeName(reference.TypeName)) return false;
            if (!reference.HasPosition) return true;
            return IsFinite(reference.PositionX) && IsFinite(reference.PositionY) &&
                IsFinite(reference.PositionZ) &&
                Math.Abs(reference.PositionX) <= MaxWorldCoordinate &&
                Math.Abs(reference.PositionZ) <= MaxWorldCoordinate &&
                Math.Abs(reference.PositionY) <= 500f;
        }

        public static bool IsSafeTypeName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxTypeNameChars) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '_') continue;
                return false;
            }
            return true;
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public static class CoopObjectResolver
    {
        public const float PositionTolerance = 0.35f;

        public static bool TryResolve(
            CoopObjectReference reference,
            IReadOnlyList<CoopObjectCandidate> candidates,
            out CoopObjectCandidate match,
            out CoopObjectResolution resolution)
        {
            match = null;
            resolution = CoopObjectResolution.Invalid;
            if (!CoopObjectReferenceValidator.IsValid(reference) || candidates == null)
                return false;

            for (int i = 0; i < candidates.Count; i++)
            {
                CoopObjectCandidate candidate = candidates[i];
                if (candidate == null || candidate.ObjectId != reference.ObjectId ||
                    !SameType(candidate, reference)) continue;
                if (reference.HasPosition && !IsNear(candidate, reference)) continue;
                match = candidate;
                resolution = CoopObjectResolution.ExactId;
                return true;
            }

            if (!reference.HasPosition)
            {
                resolution = CoopObjectResolution.Missing;
                return false;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                CoopObjectCandidate candidate = candidates[i];
                if (candidate == null || !SameType(candidate, reference) ||
                    !IsNear(candidate, reference)) continue;
                if (match != null)
                {
                    match = null;
                    resolution = CoopObjectResolution.Ambiguous;
                    return false;
                }
                match = candidate;
            }

            if (match == null)
            {
                resolution = CoopObjectResolution.Missing;
                return false;
            }
            resolution = CoopObjectResolution.UniqueSpatialFallback;
            return true;
        }

        private static bool SameType(CoopObjectCandidate candidate, CoopObjectReference reference)
        {
            return StringComparer.Ordinal.Equals(candidate.TypeName, reference.TypeName);
        }

        private static bool IsNear(CoopObjectCandidate candidate, CoopObjectReference reference)
        {
            float dx = candidate.PositionX - reference.PositionX;
            float dy = candidate.PositionY - reference.PositionY;
            float dz = candidate.PositionZ - reference.PositionZ;
            return dx * dx + dy * dy + dz * dz <= PositionTolerance * PositionTolerance;
        }
    }

    public static class CoopCheckpointSchedule
    {
        // Full-world XML serialization is synchronous in Planetbase. Running it
        // every 128 ticks caused a visible main-thread hitch roughly every 2.5s.
        // Mutation failures still trigger recovery immediately; this slower
        // cadence is only for the silent-divergence safety checksum.
        public const long DefaultBaseIntervalTicks = 512;

        public static long IntervalTicksForScale(long baseInterval, int scaleIndex)
        {
            if (baseInterval <= 0) throw new ArgumentOutOfRangeException("baseInterval");
            if (scaleIndex < 0 || scaleIndex > 2)
                throw new ArgumentOutOfRangeException("scaleIndex");
            return checked(baseInterval * (1L << scaleIndex));
        }

        public static long RebaseDeadline(
            long currentTick,
            long currentDeadline,
            long scheduledInterval,
            long currentInterval)
        {
            if (currentTick < 0) throw new ArgumentOutOfRangeException("currentTick");
            if (currentDeadline <= 0) throw new ArgumentOutOfRangeException("currentDeadline");
            if (scheduledInterval <= 0) throw new ArgumentOutOfRangeException("scheduledInterval");
            if (currentInterval <= 0) throw new ArgumentOutOfRangeException("currentInterval");
            if (currentTick >= currentDeadline) return currentDeadline;
            return scheduledInterval == currentInterval
                ? currentDeadline
                : NextBoundary(currentTick, currentInterval);
        }

        public static long NextBoundary(long currentTick, long interval)
        {
            if (currentTick < 0) throw new ArgumentOutOfRangeException("currentTick");
            if (interval <= 0) throw new ArgumentOutOfRangeException("interval");
            long quotient = currentTick / interval;
            return checked((quotient + 1) * interval);
        }

        public static bool IsBoundary(long tick, long interval)
        {
            return tick > 0 && interval > 0 && tick % interval == 0;
        }
    }

    /// <summary>
    /// Constant-space replay protection for role-tagged command sequences.
    /// Host ids are even and guest ids are odd, so each source advances an
    /// independent monotonic watermark without retaining every command id.
    /// </summary>
    public sealed class CoopSequenceWatermarks
    {
        public const long MaxForwardDistance = 16384;
        public long LastHostSequence { get; private set; }
        public long LastGuestSequence { get; private set; }

        public bool TryAccept(long sequence)
        {
            if (sequence <= 0) return false;
            bool guest = (sequence & 1L) != 0;
            if (guest)
            {
                if (sequence <= LastGuestSequence ||
                    sequence - LastGuestSequence > MaxForwardDistance) return false;
                LastGuestSequence = sequence;
                return true;
            }
            if (sequence <= LastHostSequence ||
                sequence - LastHostSequence > MaxForwardDistance) return false;
            LastHostSequence = sequence;
            return true;
        }

        public void Reset()
        {
            LastHostSequence = 0;
            LastGuestSequence = 0;
        }
    }

    public static class CoopSequencePolicy
    {
        public static bool IsFromRole(long sequence, CoopRole role)
        {
            if (sequence <= 0) return false;
            bool guest = (sequence & 1L) != 0;
            return role == CoopRole.Guest ? guest : !guest;
        }
    }

    public enum CoopMutationKind : byte
    {
        Placement = 1,
        Connection = 2,
        Recycle = 3,
        GameplayControl = 4,
        Disaster = 5,
        Ship = 6,
        Trade = 7,
        Time = 8
    }

    public enum CoopMutationFailureCode : byte
    {
        TargetMissing = 1,
        TargetAmbiguous = 2,
        ValidationRejected = 3,
        ApplyFailed = 4
    }

    public sealed class CoopMutationFailureData
    {
        public CoopMutationFailureData(
            long sessionId,
            long commandId,
            CoopMutationKind kind,
            CoopMutationFailureCode code)
        {
            SessionId = sessionId;
            CommandId = commandId;
            Kind = kind;
            Code = code;
        }

        public long SessionId { get; private set; }
        public long CommandId { get; private set; }
        public CoopMutationKind Kind { get; private set; }
        public CoopMutationFailureCode Code { get; private set; }

        public bool IsValid()
        {
            return SessionId != 0 && CommandId > 0 &&
                Kind >= CoopMutationKind.Placement && Kind <= CoopMutationKind.Time &&
                Code >= CoopMutationFailureCode.TargetMissing &&
                Code <= CoopMutationFailureCode.ApplyFailed;
        }
    }

    public sealed class CoopMutationFailureLedger
    {
        public const int MaxEntries = 64;
        private readonly Queue<string> mOrder = new Queue<string>();
        private readonly HashSet<string> mKeys = new HashSet<string>(StringComparer.Ordinal);

        public int Count { get { return mKeys.Count; } }

        public bool TryRecord(CoopMutationFailureData failure, long activeSessionId)
        {
            if (failure == null || !failure.IsValid() || activeSessionId == 0 ||
                failure.SessionId != activeSessionId) return false;
            string key = failure.SessionId.ToString(CultureInfo.InvariantCulture) + ":" +
                failure.CommandId.ToString(CultureInfo.InvariantCulture) + ":" +
                ((byte)failure.Kind).ToString(CultureInfo.InvariantCulture);
            if (!mKeys.Add(key)) return false;
            mOrder.Enqueue(key);
            while (mOrder.Count > MaxEntries)
            {
                string oldest = mOrder.Dequeue();
                mKeys.Remove(oldest);
            }
            return true;
        }

        public void Reset()
        {
            mOrder.Clear();
            mKeys.Clear();
        }
    }

    public sealed class CoopOutstandingMutationLedger
    {
        public const int MaxEntries = 256;
        private readonly object mLock = new object();
        private readonly Queue<string> mOrder = new Queue<string>();
        private readonly HashSet<string> mKeys = new HashSet<string>(StringComparer.Ordinal);

        public int Count
        {
            get { lock (mLock) return mKeys.Count; }
        }

        public void Register(long sessionId, long commandId, CoopMutationKind kind)
        {
            if (!IsValidKey(sessionId, commandId, kind)) return;
            string key = CreateKey(sessionId, commandId, kind);
            lock (mLock)
            {
                if (!mKeys.Add(key)) return;
                mOrder.Enqueue(key);
                while (mOrder.Count > MaxEntries)
                {
                    string oldest = mOrder.Dequeue();
                    mKeys.Remove(oldest);
                }
            }
        }

        public bool TryConsume(CoopMutationFailureData failure, long activeSessionId)
        {
            if (failure == null || !failure.IsValid() || activeSessionId == 0 ||
                failure.SessionId != activeSessionId) return false;
            lock (mLock)
            {
                return mKeys.Remove(CreateKey(
                    failure.SessionId,
                    failure.CommandId,
                    failure.Kind));
            }
        }

        public void Reset()
        {
            lock (mLock)
            {
                mOrder.Clear();
                mKeys.Clear();
            }
        }

        private static bool IsValidKey(long sessionId, long commandId, CoopMutationKind kind)
        {
            return sessionId != 0 && commandId > 0 &&
                kind >= CoopMutationKind.Placement && kind <= CoopMutationKind.Time;
        }

        private static string CreateKey(long sessionId, long commandId, CoopMutationKind kind)
        {
            return sessionId.ToString(CultureInfo.InvariantCulture) + ":" +
                commandId.ToString(CultureInfo.InvariantCulture) + ":" +
                ((byte)kind).ToString(CultureInfo.InvariantCulture);
        }
    }

    public enum CoopAuthoritativeCommandKind : byte
    {
        SetEnabled = 1,
        SetLocked = 2,
        SetHighPriority = 3,
        SetProducedItem = 4,
        SetAlertState = 5,
        SetGridEnabled = 6,
        SetManufactureLimit = 7,
        SetLandingPermissions = 8,
        Trade = 9,
        DisasterEvent = 10,
        VisitorEventAccept = 11,
        SetTimeState = 12
    }

    public enum CoopAuthoritativeCommandPhase : byte
    {
        Request = 1,
        Commit = 2
    }

    public enum CoopDisasterEventKind : byte
    {
        Sandstorm = 1,
        Blizzard = 2,
        SolarFlare = 3,
        Meteor = 4,
        Thunder = 5,
        MeteorConstructionImpact = 6,
        MeteorCharacterImpact = 7,
        LightningConstructionImpact = 8,
        LightningCharacterImpact = 9,
        LightningRecharge = 10
    }

    public sealed class CoopDisasterEventData
    {
        public CoopDisasterEventData(
            CoopDisasterEventKind kind,
            bool hasTarget,
            float targetX,
            float targetY,
            float targetZ,
            CoopObjectReference targetReference = null)
        {
            Kind = kind;
            HasTarget = hasTarget;
            TargetX = targetX;
            TargetY = targetY;
            TargetZ = targetZ;
            TargetReference = targetReference == null
                ? null
                : targetReference.Clone();
        }

        public CoopDisasterEventKind Kind { get; private set; }
        public bool HasTarget { get; private set; }
        public float TargetX { get; private set; }
        public float TargetY { get; private set; }
        public float TargetZ { get; private set; }
        public CoopObjectReference TargetReference { get; private set; }
    }

    public static class CoopDisasterEventCodec
    {
        private const int BasePayloadBytes = 15;

        public static byte[] Encode(CoopDisasterEventData data)
        {
            if (!IsValid(data)) throw new ArgumentException("invalid disaster event", "data");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write((byte)data.Kind);
                writer.Write((byte)(data.HasTarget ? 1 : 0));
                writer.Write(data.TargetX);
                writer.Write(data.TargetY);
                writer.Write(data.TargetZ);
                writer.Write((byte)(data.TargetReference == null ? 0 : 1));
                if (data.TargetReference != null)
                    WriteReference(writer, data.TargetReference);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryDecode(byte[] payload, out CoopDisasterEventData data)
        {
            data = null;
            try
            {
                if (payload == null || payload.Length < BasePayloadBytes ||
                    payload.Length > CoopAuthoritativeCommandData.MaxPayloadBytes)
                    return false;
                using (MemoryStream stream = new MemoryStream(payload))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    CoopDisasterEventKind kind = (CoopDisasterEventKind)reader.ReadByte();
                    byte targetByte = reader.ReadByte();
                    if (targetByte > 1) return false;
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    byte referenceByte = reader.ReadByte();
                    if (referenceByte > 1) return false;
                    CoopObjectReference reference = referenceByte == 0
                        ? null
                        : ReadReference(reader);
                    CoopDisasterEventData candidate = new CoopDisasterEventData(
                        kind, targetByte != 0, x, y, z, reference);
                    if (stream.Position != stream.Length || !IsValid(candidate)) return false;
                    data = candidate;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool IsValid(CoopDisasterEventData data)
        {
            if (data == null || data.Kind < CoopDisasterEventKind.Sandstorm ||
                data.Kind > CoopDisasterEventKind.LightningRecharge) return false;
            if (!IsFinite(data.TargetX) || !IsFinite(data.TargetY) ||
                !IsFinite(data.TargetZ) || Math.Abs(data.TargetX) > 2500f ||
                Math.Abs(data.TargetZ) > 2500f || Math.Abs(data.TargetY) > 500f)
                return false;
            bool zeroPosition = data.TargetX == 0f && data.TargetY == 0f &&
                data.TargetZ == 0f;
            bool spawn = data.Kind == CoopDisasterEventKind.Meteor ||
                data.Kind == CoopDisasterEventKind.Thunder;
            bool characterImpact =
                data.Kind == CoopDisasterEventKind.MeteorCharacterImpact ||
                data.Kind == CoopDisasterEventKind.LightningCharacterImpact;
            bool constructionImpact =
                data.Kind == CoopDisasterEventKind.MeteorConstructionImpact ||
                data.Kind == CoopDisasterEventKind.LightningConstructionImpact;
            if (spawn)
                return data.TargetReference == null &&
                    (data.HasTarget || zeroPosition);
            if (characterImpact)
                return data.HasTarget && data.TargetReference == null;
            if (constructionImpact)
                return !data.HasTarget && zeroPosition &&
                    CoopObjectReferenceValidator.IsValid(data.TargetReference) &&
                    !StringComparer.Ordinal.Equals(
                        data.TargetReference.TypeName, "Character");
            if (data.Kind == CoopDisasterEventKind.LightningRecharge)
                return !data.HasTarget && zeroPosition &&
                    CoopObjectReferenceValidator.IsValid(data.TargetReference) &&
                    StringComparer.Ordinal.Equals(
                        data.TargetReference.TypeName, "Module");
            return !data.HasTarget && zeroPosition && data.TargetReference == null;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void WriteReference(
            BinaryWriter writer,
            CoopObjectReference reference)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(reference.TypeName);
            writer.Write(reference.ObjectId);
            writer.Write((ushort)typeBytes.Length);
            writer.Write(typeBytes);
            writer.Write((byte)(reference.HasPosition ? 1 : 0));
            if (!reference.HasPosition) return;
            writer.Write(reference.PositionX);
            writer.Write(reference.PositionY);
            writer.Write(reference.PositionZ);
        }

        private static CoopObjectReference ReadReference(BinaryReader reader)
        {
            int objectId = reader.ReadInt32();
            ushort typeLength = reader.ReadUInt16();
            if (typeLength == 0 ||
                typeLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                throw new InvalidDataException("disaster target type length is invalid");
            byte[] typeBytes = reader.ReadBytes(typeLength);
            if (typeBytes.Length != typeLength) throw new EndOfStreamException();
            byte positionByte = reader.ReadByte();
            if (positionByte > 1)
                throw new InvalidDataException("disaster target position flag is invalid");
            float x = 0f;
            float y = 0f;
            float z = 0f;
            if (positionByte != 0)
            {
                x = reader.ReadSingle();
                y = reader.ReadSingle();
                z = reader.ReadSingle();
            }
            return new CoopObjectReference(
                objectId,
                Encoding.ASCII.GetString(typeBytes),
                x,
                y,
                z,
                positionByte != 0);
        }
    }

    public sealed class CoopVisitorEventCommandData
    {
        public CoopVisitorEventCommandData(string eventTypeName, int visitorCount)
        {
            EventTypeName = eventTypeName ?? string.Empty;
            VisitorCount = visitorCount;
        }

        public string EventTypeName { get; private set; }
        public int VisitorCount { get; private set; }
    }

    public static class CoopVisitorEventCommandCodec
    {
        public const int MinimumVisitorCount = 5;
        public const int MaximumVisitorCount = 15;

        public static byte[] Encode(CoopVisitorEventCommandData data)
        {
            if (!IsValid(data))
                throw new ArgumentException("invalid visitor event command", "data");
            byte[] nameBytes = Encoding.ASCII.GetBytes(data.EventTypeName);
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write((ushort)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(data.VisitorCount);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryDecode(byte[] payload, out CoopVisitorEventCommandData data)
        {
            data = null;
            try
            {
                if (payload == null || payload.Length < 7 ||
                    payload.Length > CoopObjectReferenceValidator.MaxTypeNameChars + 6)
                    return false;
                using (MemoryStream stream = new MemoryStream(payload))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    ushort nameLength = reader.ReadUInt16();
                    if (nameLength == 0 ||
                        nameLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                        return false;
                    byte[] nameBytes = reader.ReadBytes(nameLength);
                    if (nameBytes.Length != nameLength) return false;
                    CoopVisitorEventCommandData candidate =
                        new CoopVisitorEventCommandData(
                            Encoding.ASCII.GetString(nameBytes),
                            reader.ReadInt32());
                    if (stream.Position != stream.Length || !IsValid(candidate)) return false;
                    data = candidate;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool IsValid(CoopVisitorEventCommandData data)
        {
            return data != null &&
                data.VisitorCount >= MinimumVisitorCount &&
                data.VisitorCount <= MaximumVisitorCount &&
                CoopObjectReferenceValidator.IsSafeTypeName(data.EventTypeName) &&
                data.EventTypeName.StartsWith("VisitorEvent", StringComparison.Ordinal);
        }
    }

    public enum CoopTradeAction : byte
    {
        Start = 1,
        Cancel = 2,
        Dismiss = 3,
        Agree = 4
    }

    public static class CoopTradeLifecyclePolicy
    {
        public static bool CanAccept(
            CoopTradeAction action,
            bool isTrading,
            bool hasPendingStart,
            bool hasPendingTerminal,
            bool hasPendingAgreement)
        {
            if (hasPendingTerminal)
                return action == CoopTradeAction.Dismiss;
            switch (action)
            {
                case CoopTradeAction.Start:
                    return !isTrading && !hasPendingStart && !hasPendingAgreement;
                case CoopTradeAction.Cancel:
                case CoopTradeAction.Dismiss:
                    return (isTrading || hasPendingStart) && !hasPendingAgreement;
                case CoopTradeAction.Agree:
                    return (isTrading || hasPendingStart) && !hasPendingAgreement;
                default:
                    return false;
            }
        }

        public static long GetApplyTick(
            long currentTick,
            long leadTicks,
            long prerequisiteTick)
        {
            if (currentTick < 0 || leadTicks <= 0 || prerequisiteTick < 0 ||
                currentTick > CoopProtocol.MaxWorldTick - leadTicks)
                return -1;
            long applyTick = currentTick + leadTicks;
            if (prerequisiteTick >= applyTick)
            {
                if (prerequisiteTick >= CoopProtocol.MaxWorldTick) return -1;
                applyTick = prerequisiteTick + 1;
            }
            return applyTick <= CoopProtocol.MaxWorldTick ? applyTick : -1;
        }
    }

    public enum CoopTradeProductKind : byte
    {
        Resource = 1,
        Bot = 2,
        Tech = 3
    }

    public sealed class CoopTradeProductData
    {
        public CoopTradeProductData(
            CoopTradeProductKind kind,
            string productId,
            int amount)
        {
            Kind = kind;
            ProductId = productId ?? string.Empty;
            Amount = amount;
        }

        public CoopTradeProductKind Kind { get; private set; }
        public string ProductId { get; private set; }
        public int Amount { get; private set; }

        public CoopTradeProductData Clone()
        {
            return new CoopTradeProductData(Kind, ProductId, Amount);
        }
    }

    public sealed class CoopTradeCommandData
    {
        private readonly CoopTradeProductData[] mNeededProducts;
        private readonly CoopTradeProductData[] mProvidedProducts;

        public CoopTradeCommandData(
            CoopTradeAction action,
            CoopObjectReference shipReference,
            CoopTradeProductData[] neededProducts,
            CoopTradeProductData[] providedProducts)
        {
            Action = action;
            ShipReference = shipReference == null ? null : shipReference.Clone();
            mNeededProducts = CloneProducts(neededProducts);
            mProvidedProducts = CloneProducts(providedProducts);
        }

        public CoopTradeAction Action { get; private set; }
        public CoopObjectReference ShipReference { get; private set; }
        public CoopTradeProductData[] NeededProducts
        {
            get { return CloneProducts(mNeededProducts); }
        }
        public CoopTradeProductData[] ProvidedProducts
        {
            get { return CloneProducts(mProvidedProducts); }
        }

        private static CoopTradeProductData[] CloneProducts(CoopTradeProductData[] source)
        {
            if (source == null) return null;
            CoopTradeProductData[] clone = new CoopTradeProductData[source.Length];
            for (int i = 0; i < source.Length; i++)
                clone[i] = source[i] == null ? null : source[i].Clone();
            return clone;
        }
    }

    public static class CoopTradeCommandCodec
    {
        public const int MaxEntriesPerSide = 32;
        public const int MaxTotalEntries = 48;
        public const int MaxAmountPerEntry = 500;
        private const int MaxTotalAmount = 2000;

        public static byte[] Encode(CoopTradeCommandData data)
        {
            if (!IsValid(data)) throw new ArgumentException("invalid trade command", "data");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write((byte)data.Action);
                WriteReference(writer, data.ShipReference);
                WriteProducts(writer, data.NeededProducts);
                WriteProducts(writer, data.ProvidedProducts);
                writer.Flush();
                if (stream.Length > CoopAuthoritativeCommandData.MaxPayloadBytes)
                    throw new ArgumentException("trade command payload is too large", "data");
                return stream.ToArray();
            }
        }

        public static bool TryDecode(byte[] payload, out CoopTradeCommandData data)
        {
            data = null;
            try
            {
                if (payload == null || payload.Length < 12 ||
                    payload.Length > CoopAuthoritativeCommandData.MaxPayloadBytes) return false;
                using (MemoryStream stream = new MemoryStream(payload))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    CoopTradeAction action = (CoopTradeAction)reader.ReadByte();
                    CoopObjectReference ship = ReadReference(reader);
                    CoopTradeProductData[] needed = ReadProducts(reader);
                    CoopTradeProductData[] provided = ReadProducts(reader);
                    CoopTradeCommandData candidate = new CoopTradeCommandData(
                        action, ship, needed, provided);
                    if (stream.Position != stream.Length || !IsValid(candidate)) return false;
                    data = candidate;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool IsValid(CoopTradeCommandData data)
        {
            if (data == null || data.Action < CoopTradeAction.Start ||
                data.Action > CoopTradeAction.Agree ||
                !CoopObjectReferenceValidator.IsValid(data.ShipReference) ||
                !StringComparer.Ordinal.Equals(data.ShipReference.TypeName, "MerchantShip"))
                return false;
            CoopTradeProductData[] needed = data.NeededProducts;
            CoopTradeProductData[] provided = data.ProvidedProducts;
            if (!IsValidProducts(needed, true) || !IsValidProducts(provided, false) ||
                needed.Length > MaxEntriesPerSide || provided.Length > MaxEntriesPerSide ||
                needed.Length + provided.Length > MaxTotalEntries) return false;
            if (data.Action != CoopTradeAction.Agree)
                return needed.Length == 0 && provided.Length == 0;
            return needed.Length > 0 && provided.Length > 0;
        }

        private static bool IsValidProducts(
            CoopTradeProductData[] products,
            bool neededSide)
        {
            if (products == null) return false;
            int totalAmount = 0;
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < products.Length; i++)
            {
                CoopTradeProductData product = products[i];
                if (product == null || product.Kind < CoopTradeProductKind.Resource ||
                    product.Kind > CoopTradeProductKind.Tech ||
                    (neededSide && product.Kind != CoopTradeProductKind.Resource) ||
                    !CoopObjectReferenceValidator.IsSafeTypeName(product.ProductId) ||
                    product.Amount <= 0 || product.Amount > MaxAmountPerEntry) return false;
                string key = ((byte)product.Kind).ToString(CultureInfo.InvariantCulture) + ":" +
                    product.ProductId;
                if (!keys.Add(key)) return false;
                totalAmount += product.Amount;
                if (totalAmount > MaxTotalAmount) return false;
            }
            return true;
        }

        private static void WriteReference(BinaryWriter writer, CoopObjectReference reference)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(reference.TypeName);
            writer.Write(reference.ObjectId);
            writer.Write((ushort)typeBytes.Length);
            writer.Write(typeBytes);
            writer.Write((byte)(reference.HasPosition ? 1 : 0));
            if (!reference.HasPosition) return;
            writer.Write(reference.PositionX);
            writer.Write(reference.PositionY);
            writer.Write(reference.PositionZ);
        }

        private static CoopObjectReference ReadReference(BinaryReader reader)
        {
            int objectId = reader.ReadInt32();
            ushort typeLength = reader.ReadUInt16();
            if (typeLength == 0 || typeLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                throw new InvalidDataException("trade ship type length is invalid");
            byte[] typeBytes = reader.ReadBytes(typeLength);
            if (typeBytes.Length != typeLength) throw new EndOfStreamException();
            byte positionByte = reader.ReadByte();
            if (positionByte > 1) throw new InvalidDataException("trade ship position flag is invalid");
            float x = 0f;
            float y = 0f;
            float z = 0f;
            if (positionByte != 0)
            {
                x = reader.ReadSingle();
                y = reader.ReadSingle();
                z = reader.ReadSingle();
            }
            return new CoopObjectReference(
                objectId,
                Encoding.ASCII.GetString(typeBytes),
                x,
                y,
                z,
                positionByte != 0);
        }

        private static void WriteProducts(
            BinaryWriter writer,
            CoopTradeProductData[] products)
        {
            writer.Write((byte)products.Length);
            for (int i = 0; i < products.Length; i++)
            {
                byte[] idBytes = Encoding.ASCII.GetBytes(products[i].ProductId);
                writer.Write((byte)products[i].Kind);
                writer.Write((byte)idBytes.Length);
                writer.Write(idBytes);
                writer.Write(products[i].Amount);
            }
        }

        private static CoopTradeProductData[] ReadProducts(BinaryReader reader)
        {
            int count = reader.ReadByte();
            if (count > MaxEntriesPerSide) throw new InvalidDataException("too many trade products");
            CoopTradeProductData[] products = new CoopTradeProductData[count];
            for (int i = 0; i < count; i++)
            {
                CoopTradeProductKind kind = (CoopTradeProductKind)reader.ReadByte();
                int idLength = reader.ReadByte();
                if (idLength <= 0 || idLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                    throw new InvalidDataException("trade product id length is invalid");
                byte[] idBytes = reader.ReadBytes(idLength);
                if (idBytes.Length != idLength) throw new EndOfStreamException();
                products[i] = new CoopTradeProductData(
                    kind,
                    Encoding.ASCII.GetString(idBytes),
                    reader.ReadInt32());
            }
            return products;
        }
    }

    public sealed class CoopAuthoritativeCommandData
    {
        public const int MaxPayloadBytes = 1024;
        private readonly byte[] mPayload;

        public CoopAuthoritativeCommandData(
            long sessionId,
            long sequence,
            long applyTick,
            CoopAuthoritativeCommandKind kind,
            CoopAuthoritativeCommandPhase phase,
            byte[] payload)
        {
            if (payload != null && payload.Length > MaxPayloadBytes)
                throw new ArgumentException("authoritative command payload is too large", "payload");
            SessionId = sessionId;
            Sequence = sequence;
            ApplyTick = applyTick;
            Kind = kind;
            Phase = phase;
            mPayload = payload == null ? new byte[0] : (byte[])payload.Clone();
        }

        public long SessionId { get; private set; }
        public long Sequence { get; private set; }
        public long ApplyTick { get; private set; }
        public CoopAuthoritativeCommandKind Kind { get; private set; }
        public CoopAuthoritativeCommandPhase Phase { get; private set; }
        public byte[] Payload { get { return (byte[])mPayload.Clone(); } }

        public bool IsValid()
        {
            if (SessionId == 0 || Sequence <= 0 || ApplyTick < 0 ||
                ApplyTick > CoopProtocol.MaxWorldTick ||
                Kind < CoopAuthoritativeCommandKind.SetEnabled ||
                Kind > CoopAuthoritativeCommandKind.SetTimeState ||
                (Phase != CoopAuthoritativeCommandPhase.Request &&
                 Phase != CoopAuthoritativeCommandPhase.Commit)) return false;
            return Phase == CoopAuthoritativeCommandPhase.Request
                ? ApplyTick == 0
                : ApplyTick > 0;
        }
    }

    public sealed class CoopGameplayControlData
    {
        public CoopGameplayControlData(CoopObjectReference target, string key, int value)
        {
            Target = target == null ? null : target.Clone();
            Key = key ?? string.Empty;
            Value = value;
        }

        public CoopObjectReference Target { get; private set; }
        public string Key { get; private set; }
        public int Value { get; private set; }
    }

    public static class CoopGameplayControlCodec
    {
        public const int MaxKeyChars = 96;

        public static byte[] Encode(
            CoopAuthoritativeCommandKind kind,
            CoopGameplayControlData control)
        {
            if (!IsValid(kind, control))
                throw new ArgumentException("gameplay control is invalid", "control");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write((byte)(control.Target == null ? 0 : 1));
                if (control.Target != null) WriteReference(writer, control.Target);
                byte[] keyBytes = Encoding.ASCII.GetBytes(control.Key);
                writer.Write((ushort)keyBytes.Length);
                writer.Write(keyBytes);
                writer.Write(control.Value);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryDecode(
            CoopAuthoritativeCommandKind kind,
            byte[] payload,
            out CoopGameplayControlData control)
        {
            control = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(payload ?? new byte[0]))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte targetByte = reader.ReadByte();
                    if (targetByte > 1) return false;
                    CoopObjectReference target = targetByte == 0 ? null : ReadReference(reader);
                    ushort keyLength = reader.ReadUInt16();
                    if (keyLength > MaxKeyChars) return false;
                    byte[] keyBytes = reader.ReadBytes(keyLength);
                    if (keyBytes.Length != keyLength) return false;
                    int value = reader.ReadInt32();
                    if (stream.Position != stream.Length) return false;
                    CoopGameplayControlData candidate = new CoopGameplayControlData(
                        target, Encoding.ASCII.GetString(keyBytes), value);
                    if (!IsValid(kind, candidate)) return false;
                    control = candidate;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool IsValid(
            CoopAuthoritativeCommandKind kind,
            CoopGameplayControlData control)
        {
            if (control == null || control.Key.Length > MaxKeyChars) return false;
            bool requiresTarget = kind == CoopAuthoritativeCommandKind.SetEnabled ||
                kind == CoopAuthoritativeCommandKind.SetLocked ||
                kind == CoopAuthoritativeCommandKind.SetHighPriority ||
                kind == CoopAuthoritativeCommandKind.SetProducedItem ||
                kind == CoopAuthoritativeCommandKind.SetGridEnabled;
            if (requiresTarget != (control.Target != null)) return false;
            if (control.Target != null &&
                !CoopObjectReferenceValidator.IsValid(control.Target)) return false;
            if (!string.IsNullOrEmpty(control.Key) &&
                !CoopObjectReferenceValidator.IsSafeTypeName(control.Key)) return false;

            if (kind == CoopAuthoritativeCommandKind.SetManufactureLimit)
            {
                if (!IsManufactureKey(control.Key)) return false;
            }
            else if (!string.IsNullOrEmpty(control.Key))
            {
                return false;
            }

            if (kind == CoopAuthoritativeCommandKind.SetEnabled ||
                kind == CoopAuthoritativeCommandKind.SetLocked ||
                kind == CoopAuthoritativeCommandKind.SetHighPriority ||
                kind == CoopAuthoritativeCommandKind.SetGridEnabled)
                return control.Value == 0 || control.Value == 1;
            if (kind == CoopAuthoritativeCommandKind.SetProducedItem)
                return control.Value >= 0 && control.Value <= 32;
            if (kind == CoopAuthoritativeCommandKind.SetAlertState)
                return control.Value >= 0 && control.Value <= 2;
            if (kind == CoopAuthoritativeCommandKind.SetManufactureLimit)
                return control.Value == int.MaxValue ||
                    (control.Value >= 0 && control.Value <= 100);
            return false;
        }

        private static bool IsManufactureKey(string key)
        {
            const string resourcePrefix = "Resource_";
            const string botPrefix = "Bot_";
            string suffix;
            if (key != null && key.StartsWith(resourcePrefix, StringComparison.Ordinal))
                suffix = key.Substring(resourcePrefix.Length);
            else if (key != null && key.StartsWith(botPrefix, StringComparison.Ordinal))
                suffix = key.Substring(botPrefix.Length);
            else
                return false;
            return CoopObjectReferenceValidator.IsSafeTypeName(suffix);
        }

        private static void WriteReference(BinaryWriter writer, CoopObjectReference reference)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(reference.TypeName);
            writer.Write(reference.ObjectId);
            writer.Write((ushort)typeBytes.Length);
            writer.Write(typeBytes);
            writer.Write((byte)(reference.HasPosition ? 1 : 0));
            if (!reference.HasPosition) return;
            writer.Write(reference.PositionX);
            writer.Write(reference.PositionY);
            writer.Write(reference.PositionZ);
        }

        private static CoopObjectReference ReadReference(BinaryReader reader)
        {
            int objectId = reader.ReadInt32();
            ushort typeLength = reader.ReadUInt16();
            if (typeLength == 0 || typeLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                throw new InvalidDataException("control target type is invalid");
            byte[] typeBytes = reader.ReadBytes(typeLength);
            if (typeBytes.Length != typeLength) throw new EndOfStreamException();
            byte positionByte = reader.ReadByte();
            if (positionByte > 1) throw new InvalidDataException("control position flag is invalid");
            float x = 0f;
            float y = 0f;
            float z = 0f;
            if (positionByte != 0)
            {
                x = reader.ReadSingle();
                y = reader.ReadSingle();
                z = reader.ReadSingle();
            }
            return new CoopObjectReference(
                objectId,
                Encoding.ASCII.GetString(typeBytes),
                x,
                y,
                z,
                positionByte != 0);
        }
    }

    public enum CoopPlacementKind : byte
    {
        Module = 1,
        Component = 2
    }

    public sealed class CoopSelectionData
    {
        public bool Active;
        public long Sequence;
        public string TypeName;
        public int ObjectId;
        public CoopObjectReference PrimaryReference;
        public bool HasLinkTarget;
        public string LinkTypeName;
        public int LinkObjectId;
        public CoopObjectReference LinkReference;

        public CoopObjectReference GetPrimaryReference()
        {
            if (!Active) return null;
            return PrimaryReference ?? new CoopObjectReference(
                ObjectId, TypeName, 0f, 0f, 0f, false);
        }

        public CoopObjectReference GetLinkReference()
        {
            if (!HasLinkTarget) return null;
            return LinkReference ?? new CoopObjectReference(
                LinkObjectId, LinkTypeName, 0f, 0f, 0f, false);
        }
    }

    public sealed class CoopRemoteSelectionTracker
    {
        private CoopSelectionData mCurrent = new CoopSelectionData
        {
            Active = false,
            TypeName = string.Empty,
            LinkTypeName = string.Empty
        };

        public long LastSequence { get; private set; }

        public CoopSelectionData Current
        {
            get { return Clone(mCurrent); }
        }

        public bool TryAccept(CoopSelectionData selection)
        {
            if (selection == null || selection.Sequence <= LastSequence) return false;
            if (selection.Active && !CoopObjectReferenceValidator.IsValid(selection.GetPrimaryReference()))
                return false;
            if (selection.HasLinkTarget &&
                !CoopObjectReferenceValidator.IsValid(selection.GetLinkReference()))
                return false;
            LastSequence = selection.Sequence;
            mCurrent = selection.Active
                ? Clone(selection)
                : new CoopSelectionData
                {
                    Active = false,
                    Sequence = selection.Sequence,
                    TypeName = string.Empty,
                    LinkTypeName = string.Empty
                };
            return true;
        }

        public void Reset()
        {
            LastSequence = 0;
            mCurrent = new CoopSelectionData
            {
                Active = false,
                TypeName = string.Empty,
                LinkTypeName = string.Empty
            };
        }

        public static CoopSelectionData Clone(CoopSelectionData selection)
        {
            if (selection == null) return null;
            return new CoopSelectionData
            {
                Active = selection.Active,
                Sequence = selection.Sequence,
                TypeName = selection.TypeName,
                ObjectId = selection.ObjectId,
                PrimaryReference = selection.PrimaryReference == null
                    ? null
                    : selection.PrimaryReference.Clone(),
                HasLinkTarget = selection.HasLinkTarget,
                LinkTypeName = selection.LinkTypeName,
                LinkObjectId = selection.LinkObjectId,
                LinkReference = selection.LinkReference == null
                    ? null
                    : selection.LinkReference.Clone()
            };
        }
    }

    public sealed class CoopConnectionCommandData
    {
        private int mFirstModuleId;
        private int mSecondModuleId;

        public long SessionId;
        public long RequestId;
        public CoopObjectReference FirstModule;
        public CoopObjectReference SecondModule;

        public int FirstModuleId
        {
            get { return FirstModule == null ? mFirstModuleId : FirstModule.ObjectId; }
            set { mFirstModuleId = value; }
        }

        public int SecondModuleId
        {
            get { return SecondModule == null ? mSecondModuleId : SecondModule.ObjectId; }
            set { mSecondModuleId = value; }
        }

        public CoopObjectReference GetFirstModuleReference()
        {
            return FirstModule ?? new CoopObjectReference(mFirstModuleId, "Module", 0f, 0f, 0f, false);
        }

        public CoopObjectReference GetSecondModuleReference()
        {
            return SecondModule ?? new CoopObjectReference(mSecondModuleId, "Module", 0f, 0f, 0f, false);
        }

        public CoopConnectionCommandData Clone()
        {
            CoopConnectionCommandData clone = new CoopConnectionCommandData
            {
                SessionId = SessionId,
                RequestId = RequestId,
                FirstModuleId = FirstModuleId,
                SecondModuleId = SecondModuleId,
                FirstModule = FirstModule == null ? null : FirstModule.Clone(),
                SecondModule = SecondModule == null ? null : SecondModule.Clone()
            };
            return clone;
        }
    }

    public sealed class CoopRecycleCommandData
    {
        public long SessionId;
        public long RequestId;
        public int ObjectId;
        public string TypeName;
        public CoopObjectReference TargetReference;

        public CoopObjectReference GetTargetReference()
        {
            return TargetReference ?? new CoopObjectReference(
                ObjectId,
                TypeName,
                0f,
                0f,
                0f,
                false);
        }

        public CoopRecycleCommandData Clone()
        {
            return new CoopRecycleCommandData
            {
                SessionId = SessionId,
                RequestId = RequestId,
                ObjectId = ObjectId,
                TypeName = TypeName,
                TargetReference = TargetReference == null
                    ? null
                    : TargetReference.Clone()
            };
        }
    }

    public sealed class CoopPlacementPreviewData
    {
        public bool Active;
        public CoopRole Source;
        public CoopPlacementKind Kind;
        public long Sequence;
        public long ParentId;
        public CoopObjectReference ParentReference;
        public string TypeName;
        public int SizeIndex;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationY;
        public bool Valid;
    }

    public sealed class CoopPlacementCommandData
    {
        public long SessionId;
        public long RequestId;
        public CoopPlacementKind Kind;
        public long ParentId;
        public CoopObjectReference ParentReference;
        public string TypeName;
        public int SizeIndex;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationY;
        public bool Valid;

        public CoopPlacementCommandData Clone()
        {
            return new CoopPlacementCommandData
            {
                SessionId = SessionId,
                RequestId = RequestId,
                Kind = Kind,
                ParentId = ParentId,
                ParentReference = ParentReference == null ? null : ParentReference.Clone(),
                TypeName = TypeName,
                SizeIndex = SizeIndex,
                PositionX = PositionX,
                PositionY = PositionY,
                PositionZ = PositionZ,
                RotationY = RotationY,
                Valid = Valid
            };
        }
    }

    public static class CoopPlacementValidator
    {
        public const int MaxTypeNameChars = 96;
        public const float MaxWorldCoordinate = 2500f;

        public static bool TryValidate(CoopPlacementCommandData request, out CoopPlacementCommandData normalized, out string reason)
        {
            normalized = null;
            reason = null;
            if (request == null)
            {
                reason = "request is null";
                return false;
            }
            if (request.RequestId <= 0)
            {
                reason = "request id is invalid";
                return false;
            }
            if (request.Kind != CoopPlacementKind.Module && request.Kind != CoopPlacementKind.Component)
            {
                reason = "placement kind is invalid";
                return false;
            }
            if (!IsSafeTypeName(request.TypeName))
            {
                reason = "type name is invalid";
                return false;
            }
            if (request.SizeIndex < 0 || request.SizeIndex > 4)
            {
                reason = "size index is invalid";
                return false;
            }
            if (request.Kind == CoopPlacementKind.Module && request.ParentId != 0)
            {
                reason = "module parent id must be zero";
                return false;
            }
            if (request.Kind == CoopPlacementKind.Component && request.ParentId <= 0)
            {
                reason = "component parent id is required";
                return false;
            }
            if (request.Kind == CoopPlacementKind.Module && request.ParentReference != null)
            {
                reason = "module parent reference must be empty";
                return false;
            }
            if (request.ParentReference != null &&
                (!CoopObjectReferenceValidator.IsValid(request.ParentReference) ||
                 request.ParentReference.ObjectId != request.ParentId))
            {
                reason = "component parent reference is invalid";
                return false;
            }
            if (!IsFinite(request.PositionX) || !IsFinite(request.PositionY) || !IsFinite(request.PositionZ) ||
                !IsFinite(request.RotationY) || Math.Abs(request.PositionX) > MaxWorldCoordinate ||
                Math.Abs(request.PositionZ) > MaxWorldCoordinate || Math.Abs(request.PositionY) > 500f ||
                Math.Abs(request.RotationY) > 100000f)
            {
                reason = "placement coordinates are out of bounds";
                return false;
            }
            if (!request.Valid)
            {
                reason = "placement is marked invalid";
                return false;
            }

            normalized = request.Clone();
            normalized.TypeName = request.TypeName;
            normalized.RotationY = NormalizeAngle(request.RotationY);
            return true;
        }

        private static bool IsSafeTypeName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxTypeNameChars) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '_') continue;
                return false;
            }
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float NormalizeAngle(float angle)
        {
            float normalized = angle % 360f;
            if (normalized < 0f) normalized += 360f;
            return normalized;
        }
    }

    public enum CoopRole
    {
        Host,
        Guest
    }

    public enum CoopShipKind : byte
    {
        Merchant = 1,
        Colonist = 2,
        Visitor = 3
    }

    public sealed class CoopShipSpawnData
    {
        public long SessionId;
        public long Sequence;
        public long ApplyTick;
        public int ModuleId;
        public CoopObjectReference ModuleReference;
        public CoopShipKind Kind;
        public int SizeIndex;
        public int VisitorType;
        public bool Intruders;
        public string VisitorEventTypeName;
        public int VisitorCount;

        public CoopShipSpawnData Clone()
        {
            return new CoopShipSpawnData
            {
                SessionId = SessionId,
                Sequence = Sequence,
                ApplyTick = ApplyTick,
                ModuleId = ModuleId,
                ModuleReference = ModuleReference == null ? null : ModuleReference.Clone(),
                Kind = Kind,
                SizeIndex = SizeIndex,
                VisitorType = VisitorType,
                Intruders = Intruders,
                VisitorEventTypeName = VisitorEventTypeName ?? string.Empty,
                VisitorCount = VisitorCount
            };
        }
    }

    public static class CoopShipSpawnValidator
    {
        public const int RegularSize = 0;
        public const int LargeSize = 1;
        public const int CivilianVisitor = 0;
        public const int MilitaryVisitor = 1;
        public const int NotAVisitor = 2;

        public static bool TryValidate(CoopShipSpawnData spawn, out CoopShipSpawnData normalized)
        {
            normalized = null;
            if (spawn == null || spawn.SessionId == 0 ||
                spawn.Sequence <= 0 || spawn.Sequence > CoopProtocol.MaxWorldTick)
                return false;
            if (spawn.ApplyTick < 0 || spawn.ApplyTick > CoopProtocol.MaxWorldTick || spawn.ModuleId <= 0)
                return false;
            if (spawn.ModuleReference != null &&
                (!CoopObjectReferenceValidator.IsValid(spawn.ModuleReference) ||
                 spawn.ModuleReference.ObjectId != spawn.ModuleId)) return false;
            if (spawn.Kind != CoopShipKind.Merchant &&
                spawn.Kind != CoopShipKind.Colonist &&
                spawn.Kind != CoopShipKind.Visitor)
                return false;
            if (spawn.SizeIndex != RegularSize && spawn.SizeIndex != LargeSize)
                return false;

            if (spawn.Kind == CoopShipKind.Visitor)
            {
                if (spawn.VisitorType != CivilianVisitor && spawn.VisitorType != MilitaryVisitor)
                    return false;

                string eventTypeName = spawn.VisitorEventTypeName ?? string.Empty;
                bool specialEvent = eventTypeName.Length != 0;
                if (!specialEvent && spawn.VisitorCount != 0) return false;
                if (specialEvent &&
                    (!CoopObjectReferenceValidator.IsSafeTypeName(eventTypeName) ||
                     !eventTypeName.StartsWith("VisitorEvent", StringComparison.Ordinal) ||
                     spawn.VisitorCount < CoopVisitorEventCommandCodec.MinimumVisitorCount ||
                     spawn.VisitorCount > CoopVisitorEventCommandCodec.MaximumVisitorCount ||
                     spawn.SizeIndex != (spawn.VisitorCount >= 9 ? LargeSize : RegularSize) ||
                     spawn.Intruders)) return false;
            }
            else if (spawn.VisitorType != NotAVisitor)
            {
                return false;
            }
            else if (!string.IsNullOrEmpty(spawn.VisitorEventTypeName) || spawn.VisitorCount != 0)
            {
                return false;
            }
            if (spawn.Kind == CoopShipKind.Merchant && spawn.Intruders)
                return false;

            normalized = spawn.Clone();
            return true;
        }
    }

    public sealed class CoopShipSpawnQueue
    {
        public const int MaxCapacity = 1024;

        private readonly int mCapacity;
        private readonly List<CoopShipSpawnData> mPending = new List<CoopShipSpawnData>();
        private readonly HashSet<long> mPendingSequences = new HashSet<long>();
        private long mLastAcceptedSequence;
        private long mLastReleasedSequence;

        public CoopShipSpawnQueue(int capacity)
        {
            if (capacity <= 0 || capacity > MaxCapacity)
                throw new ArgumentOutOfRangeException("capacity");
            mCapacity = capacity;
        }

        public int Count
        {
            get { return mPending.Count; }
        }

        public bool TrySchedule(CoopShipSpawnData spawn, long currentTick)
        {
            CoopShipSpawnData normalized;
            if (currentTick < 0 || currentTick > CoopProtocol.MaxWorldTick ||
                !CoopShipSpawnValidator.TryValidate(spawn, out normalized) ||
                normalized.ApplyTick < currentTick ||
                normalized.ApplyTick - currentTick > CoopProtocol.MaxCommandLeadTicks ||
                normalized.Sequence != mLastAcceptedSequence + 1 ||
                normalized.Sequence <= mLastReleasedSequence ||
                mPendingSequences.Contains(normalized.Sequence) ||
                mPending.Count >= mCapacity)
                return false;

            int insertIndex = 0;
            while (insertIndex < mPending.Count && Compare(mPending[insertIndex], normalized) < 0)
                insertIndex++;
            mPending.Insert(insertIndex, normalized);
            mPendingSequences.Add(normalized.Sequence);
            mLastAcceptedSequence = normalized.Sequence;
            return true;
        }

        public bool TryDequeueDue(long currentTick, out CoopShipSpawnData spawn)
        {
            spawn = null;
            if (currentTick < 0 || currentTick > CoopProtocol.MaxWorldTick || mPending.Count == 0)
                return false;

            CoopShipSpawnData first = mPending[0];
            if (first.ApplyTick != currentTick)
                return false;

            mPending.RemoveAt(0);
            mPendingSequences.Remove(first.Sequence);
            if (first.Sequence > mLastReleasedSequence)
                mLastReleasedSequence = first.Sequence;
            spawn = first.Clone();
            return true;
        }

        public bool HasLate(long currentTick)
        {
            return currentTick >= 0 && currentTick <= CoopProtocol.MaxWorldTick &&
                mPending.Count > 0 && mPending[0].ApplyTick < currentTick;
        }

        public void Clear()
        {
            mPending.Clear();
            mPendingSequences.Clear();
            mLastAcceptedSequence = 0;
            mLastReleasedSequence = 0;
        }

        private static int Compare(CoopShipSpawnData left, CoopShipSpawnData right)
        {
            int tickComparison = left.ApplyTick.CompareTo(right.ApplyTick);
            return tickComparison != 0 ? tickComparison : left.Sequence.CompareTo(right.Sequence);
        }
    }

    public sealed class CoopShipLandingReservations
    {
        private readonly HashSet<int> mModuleIds = new HashSet<int>();

        public bool TryReserve(int moduleId)
        {
            return moduleId > 0 && mModuleIds.Add(moduleId);
        }

        public void Release(int moduleId)
        {
            if (moduleId > 0) mModuleIds.Remove(moduleId);
        }

        public void Clear()
        {
            mModuleIds.Clear();
        }
    }

    public sealed class CoopLandingPermissionsData
    {
        public long SessionId;
        public long Sequence;
        public long ApplyTick;
        public bool ColonistsAllowed;
        public bool MerchantsAllowed;
        public bool VisitorsAllowed;
        public int[] SpecializationPercentages;

        public CoopLandingPermissionsData Clone()
        {
            return new CoopLandingPermissionsData
            {
                SessionId = SessionId,
                Sequence = Sequence,
                ApplyTick = ApplyTick,
                ColonistsAllowed = ColonistsAllowed,
                MerchantsAllowed = MerchantsAllowed,
                VisitorsAllowed = VisitorsAllowed,
                SpecializationPercentages = SpecializationPercentages == null
                    ? null
                    : (int[])SpecializationPercentages.Clone()
            };
        }
    }

    public static class CoopLandingPermissionsValidator
    {
        public const int MinSpecializationCount = 1;
        public const int MaxSpecializationCount = 16;
        public const int RequiredPercentageTotal = 100;

        public static bool TryValidate(
            CoopLandingPermissionsData permissions,
            out CoopLandingPermissionsData normalized)
        {
            normalized = null;
            if (permissions == null || permissions.SessionId == 0 ||
                permissions.Sequence <= 0 ||
                permissions.Sequence > CoopProtocol.MaxWorldTick ||
                permissions.ApplyTick < 0 || permissions.ApplyTick > CoopProtocol.MaxWorldTick)
                return false;

            int[] percentages = permissions.SpecializationPercentages;
            if (percentages == null || percentages.Length < MinSpecializationCount ||
                percentages.Length > MaxSpecializationCount)
                return false;

            int total = 0;
            for (int i = 0; i < percentages.Length; i++)
            {
                int percentage = percentages[i];
                if (percentage < 0 || percentage > RequiredPercentageTotal)
                    return false;
                total += percentage;
            }
            if (total != RequiredPercentageTotal)
                return false;

            normalized = permissions.Clone();
            return true;
        }
    }

    public static class CoopLandingPermissionsCodec
    {
        private const int HeaderBytes = 4;

        public static byte[] Encode(CoopLandingPermissionsData permissions)
        {
            CoopLandingPermissionsData normalized;
            if (!TryNormalizeValues(permissions, out normalized))
                throw new ArgumentException("invalid landing permissions", "permissions");
            byte[] payload = new byte[HeaderBytes + normalized.SpecializationPercentages.Length];
            payload[0] = normalized.ColonistsAllowed ? (byte)1 : (byte)0;
            payload[1] = normalized.MerchantsAllowed ? (byte)1 : (byte)0;
            payload[2] = normalized.VisitorsAllowed ? (byte)1 : (byte)0;
            payload[3] = (byte)normalized.SpecializationPercentages.Length;
            for (int i = 0; i < normalized.SpecializationPercentages.Length; i++)
                payload[HeaderBytes + i] = (byte)normalized.SpecializationPercentages[i];
            return payload;
        }

        public static bool TryDecode(
            byte[] payload,
            out CoopLandingPermissionsData permissions)
        {
            permissions = null;
            if (payload == null || payload.Length < HeaderBytes + 1 ||
                payload[0] > 1 || payload[1] > 1 || payload[2] > 1) return false;
            int count = payload[3];
            if (count < CoopLandingPermissionsValidator.MinSpecializationCount ||
                count > CoopLandingPermissionsValidator.MaxSpecializationCount ||
                payload.Length != HeaderBytes + count) return false;
            int[] percentages = new int[count];
            for (int i = 0; i < count; i++) percentages[i] = payload[HeaderBytes + i];
            CoopLandingPermissionsData candidate = new CoopLandingPermissionsData
            {
                SessionId = 1,
                Sequence = 1,
                ApplyTick = 0,
                ColonistsAllowed = payload[0] != 0,
                MerchantsAllowed = payload[1] != 0,
                VisitorsAllowed = payload[2] != 0,
                SpecializationPercentages = percentages
            };
            return CoopLandingPermissionsValidator.TryValidate(candidate, out permissions);
        }

        private static bool TryNormalizeValues(
            CoopLandingPermissionsData source,
            out CoopLandingPermissionsData normalized)
        {
            normalized = null;
            if (source == null) return false;
            CoopLandingPermissionsData candidate = new CoopLandingPermissionsData
            {
                SessionId = 1,
                Sequence = 1,
                ApplyTick = 0,
                ColonistsAllowed = source.ColonistsAllowed,
                MerchantsAllowed = source.MerchantsAllowed,
                VisitorsAllowed = source.VisitorsAllowed,
                SpecializationPercentages = source.SpecializationPercentages == null
                    ? null
                    : (int[])source.SpecializationPercentages.Clone()
            };
            return CoopLandingPermissionsValidator.TryValidate(candidate, out normalized);
        }
    }

    public sealed class CoopLandingPermissionsQueue
    {
        public const int MaxCapacity = 1024;

        private readonly int mCapacity;
        private readonly List<CoopLandingPermissionsData> mPending =
            new List<CoopLandingPermissionsData>();
        private readonly HashSet<long> mPendingSequences = new HashSet<long>();
        private long mLastAcceptedSequence;
        private long mLastReleasedSequence;

        public CoopLandingPermissionsQueue(int capacity)
        {
            if (capacity <= 0 || capacity > MaxCapacity)
                throw new ArgumentOutOfRangeException("capacity");
            mCapacity = capacity;
        }

        public int Count
        {
            get { return mPending.Count; }
        }

        public bool TrySchedule(CoopLandingPermissionsData permissions, long currentTick)
        {
            CoopLandingPermissionsData normalized;
            if (currentTick < 0 || currentTick > CoopProtocol.MaxWorldTick ||
                !CoopLandingPermissionsValidator.TryValidate(permissions, out normalized) ||
                normalized.ApplyTick < currentTick ||
                normalized.ApplyTick - currentTick > CoopProtocol.MaxCommandLeadTicks ||
                normalized.Sequence != mLastAcceptedSequence + 1 ||
                normalized.Sequence <= mLastReleasedSequence ||
                mPendingSequences.Contains(normalized.Sequence) ||
                mPending.Count >= mCapacity)
                return false;

            int insertIndex = 0;
            while (insertIndex < mPending.Count && Compare(mPending[insertIndex], normalized) < 0)
                insertIndex++;
            mPending.Insert(insertIndex, normalized);
            mPendingSequences.Add(normalized.Sequence);
            mLastAcceptedSequence = normalized.Sequence;
            return true;
        }

        public bool TryDequeueDue(long currentTick, out CoopLandingPermissionsData permissions)
        {
            permissions = null;
            if (currentTick < 0 || currentTick > CoopProtocol.MaxWorldTick || mPending.Count == 0)
                return false;

            CoopLandingPermissionsData first = mPending[0];
            if (first.ApplyTick != currentTick)
                return false;

            mPending.RemoveAt(0);
            mPendingSequences.Remove(first.Sequence);
            if (first.Sequence > mLastReleasedSequence)
                mLastReleasedSequence = first.Sequence;
            permissions = first.Clone();
            return true;
        }

        public bool HasLate(long currentTick)
        {
            return currentTick >= 0 && currentTick <= CoopProtocol.MaxWorldTick &&
                mPending.Count > 0 && mPending[0].ApplyTick < currentTick;
        }

        public bool HasPendingValues(CoopLandingPermissionsData permissions)
        {
            if (permissions == null) return false;
            for (int i = 0; i < mPending.Count; i++)
            {
                if (SameValues(mPending[i], permissions)) return true;
            }
            return false;
        }

        public void Clear()
        {
            mPending.Clear();
            mPendingSequences.Clear();
            mLastAcceptedSequence = 0;
            mLastReleasedSequence = 0;
        }

        private static int Compare(
            CoopLandingPermissionsData left,
            CoopLandingPermissionsData right)
        {
            int tickComparison = left.ApplyTick.CompareTo(right.ApplyTick);
            return tickComparison != 0
                ? tickComparison
                : left.Sequence.CompareTo(right.Sequence);
        }

        private static bool SameValues(
            CoopLandingPermissionsData left,
            CoopLandingPermissionsData right)
        {
            if (left == null || right == null ||
                left.ColonistsAllowed != right.ColonistsAllowed ||
                left.MerchantsAllowed != right.MerchantsAllowed ||
                left.VisitorsAllowed != right.VisitorsAllowed ||
                left.SpecializationPercentages == null ||
                right.SpecializationPercentages == null ||
                left.SpecializationPercentages.Length != right.SpecializationPercentages.Length)
                return false;
            for (int i = 0; i < left.SpecializationPercentages.Length; i++)
            {
                if (left.SpecializationPercentages[i] !=
                    right.SpecializationPercentages[i]) return false;
            }
            return true;
        }
    }

    public static class CoopShipAuthorityPolicy
    {
        public static bool AllowLocalShipControl(bool fullMode, string role)
        {
            return !fullMode || !StringComparer.OrdinalIgnoreCase.Equals(role, "guest");
        }

        public static bool AllowAutomaticSpawn(bool fullMode, string role)
        {
            return !fullMode || StringComparer.OrdinalIgnoreCase.Equals(role, "host");
        }

        public static bool AllowMerchantTrading(bool fullMode, string role)
        {
            return true;
        }
    }

    public static class CoopCommandSessionPolicy
    {
        public static bool CanAccept(
            bool sessionRunning,
            long activeSessionId,
            long commandSessionId)
        {
            return sessionRunning && activeSessionId != 0 &&
                commandSessionId == activeSessionId;
        }
    }

    public enum CoopGameOverRequestDecision
    {
        IgnoreInvalidState,
        IgnoreBusy,
        IgnoreTerminal,
        RecoverGuest,
        PublishTerminal
    }

    public static class CoopGameOverPolicy
    {
        public static CoopGameOverRequestDecision DecideHostRequest(
            bool sessionMatches,
            bool sessionRunning,
            bool worldLoaded,
            bool activeGameState,
            bool recoveryPending,
            bool sessionTerminal,
            bool hostTerminalCondition)
        {
            if (sessionTerminal)
                return CoopGameOverRequestDecision.IgnoreTerminal;
            if (!sessionMatches || !sessionRunning || !worldLoaded || !activeGameState)
                return CoopGameOverRequestDecision.IgnoreInvalidState;
            if (hostTerminalCondition)
                return CoopGameOverRequestDecision.PublishTerminal;
            if (recoveryPending)
                return CoopGameOverRequestDecision.IgnoreBusy;
            return CoopGameOverRequestDecision.RecoverGuest;
        }

        public static bool ShouldSendGuestRequest(
            bool sessionRunning,
            bool worldLoaded,
            bool sessionTerminal,
            bool authoritativeStatePending,
            bool alreadySent)
        {
            return sessionRunning && worldLoaded && !sessionTerminal &&
                !authoritativeStatePending && !alreadySent;
        }

        public static bool IsTerminalSession(
            long terminalSessionId,
            long activeSessionId)
        {
            return terminalSessionId != 0 && terminalSessionId == activeSessionId;
        }

        public static bool IsPlausibleRequestTick(long requestTick, long hostTick)
        {
            return CoopProtocol.IsPlausibleHashTick(requestTick, hostTick);
        }
    }

    public static class CoopGameStateTransitionPolicy
    {
        public static bool ShouldAllowTitleTransition(
            bool fullMode,
            bool isGuest,
            bool snapshotInstallInProgress,
            bool authorizedTitleTransition)
        {
            return authorizedTitleTransition || !fullMode || !isGuest ||
                !snapshotInstallInProgress;
        }

        public static bool ShouldInvalidateWorld(
            bool previousWasGameState,
            bool currentIsGameState)
        {
            return previousWasGameState && !currentIsGameState;
        }
    }

    public static class CoopLandingTargetPolicy
    {
        public static bool IsValid(
            int sizeIndex,
            bool isLandingPad,
            bool isStarport,
            bool isOperational,
            bool isFree)
        {
            if ((sizeIndex != CoopShipSpawnValidator.RegularSize &&
                    sizeIndex != CoopShipSpawnValidator.LargeSize) ||
                (!isLandingPad && !isStarport) || !isOperational || !isFree)
                return false;
            return sizeIndex != CoopShipSpawnValidator.LargeSize || isStarport;
        }
    }

    public enum CoopTimeActor : byte
    {
        System = 0,
        Host = 1,
        Guest = 2
    }

    public enum CoopTimeOperation : byte
    {
        IncreaseSpeed = 0,
        DecreaseSpeed = 1,
        NormalSpeed = 2,
        Pause = 3,
        Unpause = 4
    }

    public sealed class CoopTimeStateData
    {
        public CoopTimeStateData(bool paused, int scaleIndex, CoopTimeActor actor)
        {
            Paused = paused;
            ScaleIndex = scaleIndex;
            Actor = actor;
        }

        public bool Paused { get; private set; }
        public int ScaleIndex { get; private set; }
        public CoopTimeActor Actor { get; private set; }
    }

    public static class CoopTimeStateTransition
    {
        public static CoopTimeStateData Apply(
            bool paused,
            int scaleIndex,
            CoopTimeOperation operation,
            CoopTimeActor actor)
        {
            if (scaleIndex < 0 || scaleIndex > 2)
                throw new ArgumentOutOfRangeException("scaleIndex");
            if (operation < CoopTimeOperation.IncreaseSpeed ||
                operation > CoopTimeOperation.Unpause)
                throw new ArgumentOutOfRangeException("operation");
            if (actor != CoopTimeActor.Host && actor != CoopTimeActor.Guest)
                throw new ArgumentOutOfRangeException("actor");

            switch (operation)
            {
                case CoopTimeOperation.IncreaseSpeed:
                    scaleIndex = Math.Min(2, scaleIndex + 1);
                    break;
                case CoopTimeOperation.DecreaseSpeed:
                    scaleIndex = Math.Max(0, scaleIndex - 1);
                    break;
                case CoopTimeOperation.NormalSpeed:
                    scaleIndex = 0;
                    break;
                case CoopTimeOperation.Pause:
                    paused = true;
                    break;
                case CoopTimeOperation.Unpause:
                    paused = false;
                    break;
            }
            return new CoopTimeStateData(paused, scaleIndex, actor);
        }
    }

    public static class CoopTimeCommandCodec
    {
        private const byte CommitMarker = 0xA5;
        private const byte OperationCommitMarker = 0xA6;

        public static byte[] EncodeRequest(CoopTimeOperation operation)
        {
            if (operation < CoopTimeOperation.IncreaseSpeed ||
                operation > CoopTimeOperation.Unpause)
                throw new ArgumentOutOfRangeException("operation");
            return new[] { (byte)operation };
        }

        public static bool TryDecodeRequest(byte[] payload, out CoopTimeOperation operation)
        {
            operation = CoopTimeOperation.IncreaseSpeed;
            if (payload == null || payload.Length != 1 ||
                payload[0] > (byte)CoopTimeOperation.Unpause) return false;
            operation = (CoopTimeOperation)payload[0];
            return true;
        }

        public static byte[] EncodeCommit(CoopTimeStateData state)
        {
            if (!IsValid(state)) throw new ArgumentException("invalid time state", "state");
            return new[]
            {
                CommitMarker,
                (byte)(state.Paused ? 1 : 0),
                (byte)state.ScaleIndex,
                (byte)state.Actor
            };
        }

        public static bool TryDecodeCommit(byte[] payload, out CoopTimeStateData state)
        {
            state = null;
            if (payload == null || payload.Length != 4 ||
                payload[0] != CommitMarker || payload[1] > 1 || payload[2] > 2 ||
                payload[3] > (byte)CoopTimeActor.Guest) return false;
            state = new CoopTimeStateData(
                payload[1] != 0,
                payload[2],
                (CoopTimeActor)payload[3]);
            return true;
        }

        public static byte[] EncodeOperationCommit(
            CoopTimeOperation operation,
            CoopTimeActor actor)
        {
            if (operation < CoopTimeOperation.IncreaseSpeed ||
                operation > CoopTimeOperation.Unpause)
                throw new ArgumentOutOfRangeException("operation");
            if (actor != CoopTimeActor.Host && actor != CoopTimeActor.Guest)
                throw new ArgumentOutOfRangeException("actor");
            return new[] { OperationCommitMarker, (byte)operation, (byte)actor };
        }

        public static bool TryDecodeOperationCommit(
            byte[] payload,
            out CoopTimeOperation operation,
            out CoopTimeActor actor)
        {
            operation = CoopTimeOperation.IncreaseSpeed;
            actor = CoopTimeActor.System;
            if (payload == null || payload.Length != 3 ||
                payload[0] != OperationCommitMarker ||
                payload[1] > (byte)CoopTimeOperation.Unpause ||
                (payload[2] != (byte)CoopTimeActor.Host &&
                 payload[2] != (byte)CoopTimeActor.Guest)) return false;
            operation = (CoopTimeOperation)payload[1];
            actor = (CoopTimeActor)payload[2];
            return true;
        }

        private static bool IsValid(CoopTimeStateData state)
        {
            return state != null && state.ScaleIndex >= 0 && state.ScaleIndex <= 2 &&
                state.Actor >= CoopTimeActor.System && state.Actor <= CoopTimeActor.Guest;
        }
    }

    public enum CoopGameOverPacketKind : byte
    {
        Request = 1,
        State = 2
    }

    public enum CoopStartupPhase
    {
        WaitingForLocalWorld,
        WaitingForPeer,
        ReadyToStart,
        Running,
        Failed
    }

    public static class CoopProtocol
    {
        public const byte PktWorld = 1;
        public const byte PktHash = 2;
        public const byte PktTick = 3;
        public const byte PktReady = 5;
        public const byte PktStart = 6;
        public const byte PktAbort = 7;
        public const byte PktPlacementPreview = 8;
        public const byte PktPlacementRequest = 9;
        public const byte PktPlacementResult = 10;
        public const byte PktTimeState = 11;
        public const byte PktTimeStateRequest = 12;
        public const byte PktSelection = 13;
        public const byte PktConnectionRequest = 14;
        public const byte PktConnectionResult = 15;
        public const byte PktHeartbeat = 16;
        public const byte PktRecycleRequest = 17;
        public const byte PktRecycleResult = 18;
        public const byte PktGameOverRequest = 19;
        public const byte PktGameOverState = 20;
        public const byte PktShipSpawn = 21;
        public const byte PktLandingPermissions = 22;
        public const byte PktMutationFailure = 23;
        public const byte PktAuthoritativeCommand = 24;
        public const int MaxPacketBytes = 16 * 1024 * 1024;
        public const int MaxControlPacketBytes = 64 * 1024;
        public const int WorldHashChars = 16;
        public const long HashTickWindow = 512;
        public const long MaxWorldTick = int.MaxValue - 1L;
        public const long MaxCommandLeadTicks = 64;
        private const int ReadyHeaderBytes = 1 + 8 + 8 + 2;
        private const int StartPacketBytes = 1 + 8 + 8 + 1 + 1;
        private const int HashPacketBytes = 1 + 8 + WorldHashChars;
        private const int TickPacketBytes = 1 + 8;
        private const int ShipSpawnHeaderBytes = 1 + 8 + 8 + 8 + 4 + 1 + 1 + 1 + 1;
        private const int LandingPermissionsHeaderBytes = 1 + 8 + 8 + 8 + 1 + 1 + 1 + 1;
        private const int MutationFailurePacketBytes = 1 + 8 + 8 + 1 + 1;
        private const int AuthoritativeCommandHeaderBytes = 1 + 8 + 8 + 8 + 1 + 1 + 2;
        private const int HeartbeatPacketBytes = 1 + 1 + 8 + 8;
        private const int MaxHashChars = 128;
        private const int MaxProfileChars = 64;

        public static bool IsValidFrameLength(int length)
        {
            return length > 0 && length <= MaxPacketBytes;
        }

        public static bool IsValidFrameLengthForType(byte packetType, int length)
        {
            if (!IsValidFrameLength(length)) return false;
            if (packetType == PktWorld)
                return length <= CoopSnapshotInvariant.WorldPacketHeaderBytes +
                    CoopSnapshotInvariant.MaxSnapshotBytes;
            return packetType >= PktHash &&
                packetType <= CoopLobbyProtocol.PktLobbySelectionLoaded &&
                length <= MaxControlPacketBytes;
        }

        public static byte[] CreateHeartbeatPacket(
            bool reply,
            long sequence,
            long sentUtcTicks)
        {
            if (sequence <= 0) throw new ArgumentOutOfRangeException("sequence");
            if (sentUtcTicks <= 0) throw new ArgumentOutOfRangeException("sentUtcTicks");
            byte[] packet = new byte[HeartbeatPacketBytes];
            packet[0] = PktHeartbeat;
            packet[1] = reply ? (byte)1 : (byte)0;
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, packet, 2, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(sentUtcTicks), 0, packet, 10, 8);
            return packet;
        }

        public static bool TryParseHeartbeatPacket(
            byte[] packet,
            out bool reply,
            out long sequence,
            out long sentUtcTicks)
        {
            reply = false;
            sequence = 0;
            sentUtcTicks = 0;
            if (packet == null || packet.Length != HeartbeatPacketBytes ||
                packet[0] != PktHeartbeat || packet[1] > 1) return false;
            sequence = BitConverter.ToInt64(packet, 2);
            sentUtcTicks = BitConverter.ToInt64(packet, 10);
            if (sequence <= 0 || sentUtcTicks <= 0) return false;
            reply = packet[1] == 1;
            return true;
        }

        public static byte[] CreateMutationFailurePacket(CoopMutationFailureData failure)
        {
            if (failure == null || !failure.IsValid())
                throw new ArgumentException("mutation failure is invalid", "failure");
            byte[] packet = new byte[MutationFailurePacketBytes];
            packet[0] = PktMutationFailure;
            Buffer.BlockCopy(BitConverter.GetBytes(failure.SessionId), 0, packet, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(failure.CommandId), 0, packet, 9, 8);
            packet[17] = (byte)failure.Kind;
            packet[18] = (byte)failure.Code;
            return packet;
        }

        public static bool TryParseMutationFailurePacket(
            byte[] packet,
            out CoopMutationFailureData failure)
        {
            failure = null;
            if (packet == null || packet.Length != MutationFailurePacketBytes ||
                packet[0] != PktMutationFailure) return false;
            CoopMutationFailureData candidate = new CoopMutationFailureData(
                BitConverter.ToInt64(packet, 1),
                BitConverter.ToInt64(packet, 9),
                (CoopMutationKind)packet[17],
                (CoopMutationFailureCode)packet[18]);
            if (!candidate.IsValid()) return false;
            failure = candidate;
            return true;
        }

        public static bool TryDescribeCommittedMutation(
            byte[] packet,
            out long sessionId,
            out long commandId,
            out CoopMutationKind mutationKind)
        {
            sessionId = 0;
            commandId = 0;
            mutationKind = 0;
            if (packet == null || packet.Length < 1) return false;

            CoopAuthoritativeCommandData authoritative;
            if (packet[0] == PktAuthoritativeCommand &&
                TryParseAuthoritativeCommandPacket(packet, out authoritative) &&
                authoritative.Phase == CoopAuthoritativeCommandPhase.Commit)
            {
                switch (authoritative.Kind)
                {
                    case CoopAuthoritativeCommandKind.Trade:
                        mutationKind = CoopMutationKind.Trade;
                        break;
                    case CoopAuthoritativeCommandKind.DisasterEvent:
                        mutationKind = CoopMutationKind.Disaster;
                        break;
                    case CoopAuthoritativeCommandKind.VisitorEventAccept:
                        mutationKind = CoopMutationKind.Ship;
                        break;
                    case CoopAuthoritativeCommandKind.SetTimeState:
                        mutationKind = CoopMutationKind.Time;
                        break;
                    default:
                        mutationKind = CoopMutationKind.GameplayControl;
                        break;
                }
                sessionId = authoritative.SessionId;
                commandId = authoritative.Sequence;
                return true;
            }

            bool accepted;
            long applyTick;
            CoopPlacementCommandData placement;
            if (packet[0] == PktPlacementResult &&
                TryParsePlacementResultPacket(
                    packet, out accepted, out applyTick, out placement) &&
                accepted && applyTick > 0)
            {
                sessionId = placement.SessionId;
                commandId = placement.RequestId;
                mutationKind = CoopMutationKind.Placement;
                return true;
            }

            CoopConnectionCommandData connection;
            if (packet[0] == PktConnectionResult &&
                TryParseConnectionResultPacket(
                    packet, out accepted, out applyTick, out connection) &&
                accepted && applyTick > 0)
            {
                sessionId = connection.SessionId;
                commandId = connection.RequestId;
                mutationKind = CoopMutationKind.Connection;
                return true;
            }

            CoopRecycleCommandData recycle;
            if (packet[0] == PktRecycleResult &&
                TryParseRecycleResultPacket(
                    packet, out accepted, out applyTick, out recycle) &&
                accepted && applyTick > 0)
            {
                sessionId = recycle.SessionId;
                commandId = recycle.RequestId;
                mutationKind = CoopMutationKind.Recycle;
                return true;
            }

            CoopShipSpawnData spawn;
            if (packet[0] == PktShipSpawn &&
                TryParseShipSpawnPacket(packet, out spawn))
            {
                sessionId = spawn.SessionId;
                commandId = spawn.Sequence;
                mutationKind = CoopMutationKind.Ship;
                return true;
            }
            return false;
        }

        public static byte[] CreateAuthoritativeCommandPacket(
            CoopAuthoritativeCommandData command)
        {
            if (command == null || !command.IsValid())
                throw new ArgumentException("authoritative command is invalid", "command");
            byte[] payload = command.Payload;
            byte[] packet = new byte[AuthoritativeCommandHeaderBytes + payload.Length];
            packet[0] = PktAuthoritativeCommand;
            Buffer.BlockCopy(BitConverter.GetBytes(command.SessionId), 0, packet, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(command.Sequence), 0, packet, 9, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(command.ApplyTick), 0, packet, 17, 8);
            packet[25] = (byte)command.Kind;
            packet[26] = (byte)command.Phase;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)payload.Length), 0, packet, 27, 2);
            Buffer.BlockCopy(payload, 0, packet, AuthoritativeCommandHeaderBytes, payload.Length);
            return packet;
        }

        public static bool TryParseAuthoritativeCommandPacket(
            byte[] packet,
            out CoopAuthoritativeCommandData command)
        {
            command = null;
            if (packet == null || packet.Length < AuthoritativeCommandHeaderBytes ||
                packet[0] != PktAuthoritativeCommand) return false;
            ushort payloadLength = BitConverter.ToUInt16(packet, 27);
            if (payloadLength > CoopAuthoritativeCommandData.MaxPayloadBytes ||
                packet.Length != AuthoritativeCommandHeaderBytes + payloadLength) return false;
            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(packet, AuthoritativeCommandHeaderBytes, payload, 0, payloadLength);
            CoopAuthoritativeCommandData candidate;
            try
            {
                candidate = new CoopAuthoritativeCommandData(
                    BitConverter.ToInt64(packet, 1),
                    BitConverter.ToInt64(packet, 9),
                    BitConverter.ToInt64(packet, 17),
                    (CoopAuthoritativeCommandKind)packet[25],
                    (CoopAuthoritativeCommandPhase)packet[26],
                    payload);
            }
            catch (ArgumentException) { return false; }
            if (!candidate.IsValid()) return false;
            command = candidate;
            return true;
        }

        public static bool IsSafeProfileName(string profile)
        {
            if (string.IsNullOrEmpty(profile) || profile.Length > MaxProfileChars || profile == "." || profile == "..")
                return false;
            for (int i = 0; i < profile.Length; i++)
            {
                char c = profile[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '_')
                    continue;
                return false;
            }
            return true;
        }

        public static byte[] CreateHashPacket(long tick, string hash)
        {
            if (!IsWorldHash(hash)) throw new ArgumentException("World hash must be 16 lowercase hex characters.", "hash");
            byte[] packet = new byte[HashPacketBytes];
            packet[0] = PktHash;
            Buffer.BlockCopy(BitConverter.GetBytes(tick), 0, packet, 1, 8);
            Encoding.ASCII.GetBytes(hash, 0, hash.Length, packet, 9);
            return packet;
        }

        public static bool TryParseHashPacket(byte[] packet, out long tick, out string hash)
        {
            tick = 0;
            hash = null;
            if (packet == null || packet.Length != HashPacketBytes || packet[0] != PktHash)
                return false;
            tick = BitConverter.ToInt64(packet, 1);
            hash = Encoding.ASCII.GetString(packet, 9, WorldHashChars);
            return tick >= 0 && IsWorldHash(hash);
        }

        public static bool IsPlausibleHashTick(long peerTick, long localTick)
        {
            if (peerTick < 0 || localTick < 0) return false;
            long minimum = localTick > HashTickWindow ? localTick - HashTickWindow : 0;
            long maximum = localTick > long.MaxValue - HashTickWindow
                ? long.MaxValue
                : localTick + HashTickWindow;
            return peerTick >= minimum && peerTick <= maximum;
        }

        public static byte[] CreateTickPacket(long tick)
        {
            if (tick < 0) throw new ArgumentOutOfRangeException("tick");
            byte[] packet = new byte[TickPacketBytes];
            packet[0] = PktTick;
            Buffer.BlockCopy(BitConverter.GetBytes(tick), 0, packet, 1, 8);
            return packet;
        }

        public static bool TryParseTickPacket(byte[] packet, out long tick)
        {
            tick = 0;
            if (packet == null || packet.Length != TickPacketBytes || packet[0] != PktTick)
                return false;
            tick = BitConverter.ToInt64(packet, 1);
            return tick >= 0;
        }

        public static bool IsValidWorldTick(long tick, long localTick, bool recovery)
        {
            if (tick < 0 || tick > MaxWorldTick) return false;
            return !recovery || IsPlausibleHashTick(tick, localTick);
        }

        public static byte[] CreateShipSpawnPacket(CoopShipSpawnData spawn)
        {
            CoopShipSpawnData normalized;
            if (!CoopShipSpawnValidator.TryValidate(spawn, out normalized))
                throw new ArgumentException("Ship spawn data is invalid.", "spawn");

            CoopObjectReference moduleReference = normalized.ModuleReference ??
                new CoopObjectReference(normalized.ModuleId, "Module", 0f, 0f, 0f, false);
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktShipSpawn);
                writer.Write(normalized.SessionId);
                writer.Write(normalized.Sequence);
                writer.Write(normalized.ApplyTick);
                writer.Write(normalized.ModuleId);
                writer.Write((byte)normalized.Kind);
                writer.Write((byte)normalized.SizeIndex);
                writer.Write((byte)normalized.VisitorType);
                writer.Write((byte)(normalized.Intruders ? 1 : 0));
                WriteObjectReference(writer, moduleReference);
                byte[] eventTypeBytes = Encoding.ASCII.GetBytes(
                    normalized.VisitorEventTypeName ?? string.Empty);
                writer.Write((ushort)eventTypeBytes.Length);
                writer.Write(eventTypeBytes);
                writer.Write(normalized.VisitorCount);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParseShipSpawnPacket(byte[] packet, out CoopShipSpawnData spawn)
        {
            spawn = null;
            if (packet == null || packet.Length <= ShipSpawnHeaderBytes ||
                packet[0] != PktShipSpawn || packet[32] > 1)
                return false;
            try
            {
                using (MemoryStream stream = new MemoryStream(packet))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    stream.Position = ShipSpawnHeaderBytes;
                    CoopObjectReference moduleReference = ReadObjectReference(reader);
                    ushort eventTypeLength = reader.ReadUInt16();
                    if (eventTypeLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                        return false;
                    byte[] eventTypeBytes = reader.ReadBytes(eventTypeLength);
                    if (eventTypeBytes.Length != eventTypeLength) return false;
                    int visitorCount = reader.ReadInt32();
                    if (stream.Position != stream.Length) return false;
                    CoopShipSpawnData candidate = new CoopShipSpawnData
                    {
                        SessionId = BitConverter.ToInt64(packet, 1),
                        Sequence = BitConverter.ToInt64(packet, 9),
                        ApplyTick = BitConverter.ToInt64(packet, 17),
                        ModuleId = BitConverter.ToInt32(packet, 25),
                        ModuleReference = moduleReference,
                        Kind = (CoopShipKind)packet[29],
                        SizeIndex = packet[30],
                        VisitorType = packet[31],
                        Intruders = packet[32] != 0,
                        VisitorEventTypeName = Encoding.ASCII.GetString(eventTypeBytes),
                        VisitorCount = visitorCount
                    };
                    return CoopShipSpawnValidator.TryValidate(candidate, out spawn);
                }
            }
            catch (Exception) { return false; }
        }

        public static byte[] CreateLandingPermissionsPacket(
            CoopLandingPermissionsData permissions)
        {
            CoopLandingPermissionsData normalized;
            if (!CoopLandingPermissionsValidator.TryValidate(permissions, out normalized))
                throw new ArgumentException("Landing permissions data is invalid.", "permissions");

            int specializationCount = normalized.SpecializationPercentages.Length;
            byte[] packet = new byte[LandingPermissionsHeaderBytes + specializationCount];
            packet[0] = PktLandingPermissions;
            Buffer.BlockCopy(BitConverter.GetBytes(normalized.SessionId), 0, packet, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(normalized.Sequence), 0, packet, 9, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(normalized.ApplyTick), 0, packet, 17, 8);
            packet[25] = normalized.ColonistsAllowed ? (byte)1 : (byte)0;
            packet[26] = normalized.MerchantsAllowed ? (byte)1 : (byte)0;
            packet[27] = normalized.VisitorsAllowed ? (byte)1 : (byte)0;
            packet[28] = (byte)specializationCount;
            for (int i = 0; i < specializationCount; i++)
                packet[29 + i] = (byte)normalized.SpecializationPercentages[i];
            return packet;
        }

        public static bool TryParseLandingPermissionsPacket(
            byte[] packet,
            out CoopLandingPermissionsData permissions)
        {
            permissions = null;
            if (packet == null || packet.Length < LandingPermissionsHeaderBytes + 1 ||
                packet[0] != PktLandingPermissions || packet[25] > 1 || packet[26] > 1 ||
                packet[27] > 1)
                return false;

            int specializationCount = packet[28];
            if (specializationCount < CoopLandingPermissionsValidator.MinSpecializationCount ||
                specializationCount > CoopLandingPermissionsValidator.MaxSpecializationCount ||
                packet.Length != LandingPermissionsHeaderBytes + specializationCount)
                return false;

            int[] percentages = new int[specializationCount];
            for (int i = 0; i < specializationCount; i++)
                percentages[i] = packet[29 + i];

            CoopLandingPermissionsData candidate = new CoopLandingPermissionsData
            {
                SessionId = BitConverter.ToInt64(packet, 1),
                Sequence = BitConverter.ToInt64(packet, 9),
                ApplyTick = BitConverter.ToInt64(packet, 17),
                ColonistsAllowed = packet[25] != 0,
                MerchantsAllowed = packet[26] != 0,
                VisitorsAllowed = packet[27] != 0,
                SpecializationPercentages = percentages
            };
            return CoopLandingPermissionsValidator.TryValidate(candidate, out permissions);
        }

        private static bool IsWorldHash(string hash)
        {
            if (hash == null || hash.Length != WorldHashChars) return false;
            for (int i = 0; i < hash.Length; i++)
            {
                char c = hash[i];
                if ((c < '0' || c > '9') && (c < 'a' || c > 'f')) return false;
            }
            return true;
        }

        public static byte[] CreateReadyPacket(long sessionId, string worldHash, long tick)
        {
            if (worldHash == null) throw new ArgumentNullException("worldHash");
            string boundedHash = worldHash.Length > MaxHashChars
                ? worldHash.Substring(0, MaxHashChars)
                : worldHash;
            byte[] hashBytes = Encoding.ASCII.GetBytes(boundedHash);
            byte[] packet = new byte[ReadyHeaderBytes + hashBytes.Length];
            packet[0] = PktReady;
            Buffer.BlockCopy(BitConverter.GetBytes(sessionId), 0, packet, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(tick), 0, packet, 9, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)hashBytes.Length), 0, packet, 17, 2);
            Buffer.BlockCopy(hashBytes, 0, packet, ReadyHeaderBytes, hashBytes.Length);
            return packet;
        }

        public static bool TryParseReadyPacket(byte[] packet, out long sessionId, out string worldHash, out long tick)
        {
            sessionId = 0;
            worldHash = null;
            tick = 0;
            if (packet == null || packet.Length < ReadyHeaderBytes || packet[0] != PktReady)
                return false;

            ushort hashLength = BitConverter.ToUInt16(packet, 17);
            if (hashLength > MaxHashChars || packet.Length != ReadyHeaderBytes + hashLength)
                return false;

            sessionId = BitConverter.ToInt64(packet, 1);
            tick = BitConverter.ToInt64(packet, 9);
            worldHash = Encoding.ASCII.GetString(packet, ReadyHeaderBytes, hashLength);
            return true;
        }

        public static byte[] CreateStartPacket(
            long sessionId,
            long tick,
            bool paused,
            int scaleIndex)
        {
            if (sessionId == 0 || tick < 0 || tick > MaxWorldTick ||
                scaleIndex < 0 || scaleIndex > 2)
                throw new ArgumentOutOfRangeException("invalid start state");
            byte[] packet = new byte[StartPacketBytes];
            packet[0] = PktStart;
            Buffer.BlockCopy(BitConverter.GetBytes(sessionId), 0, packet, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(tick), 0, packet, 9, 8);
            packet[17] = (byte)(paused ? 1 : 0);
            packet[18] = (byte)scaleIndex;
            return packet;
        }

        public static bool TryParseStartPacket(
            byte[] packet,
            out long sessionId,
            out long tick,
            out bool paused,
            out int scaleIndex)
        {
            sessionId = 0;
            tick = 0;
            paused = false;
            scaleIndex = 0;
            if (packet == null || packet.Length != StartPacketBytes ||
                packet[0] != PktStart || packet[17] > 1 || packet[18] > 2)
                return false;
            sessionId = BitConverter.ToInt64(packet, 1);
            tick = BitConverter.ToInt64(packet, 9);
            paused = packet[17] != 0;
            scaleIndex = packet[18];
            return sessionId != 0 && tick >= 0 && tick <= MaxWorldTick;
        }

        public static byte[] CreatePreviewPacket(CoopPlacementPreviewData preview)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktPlacementPreview);
                writer.Write(preview.Active);
                writer.Write((byte)preview.Source);
                WritePlacementCore(writer, preview.Kind, preview.Sequence, preview.ParentId, preview.ParentReference, preview.TypeName,
                    preview.SizeIndex, preview.PositionX, preview.PositionY, preview.PositionZ, preview.RotationY, preview.Valid);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParsePreviewPacket(byte[] packet, out CoopPlacementPreviewData preview)
        {
            preview = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(packet ?? new byte[0]))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != PktPlacementPreview) return false;
                    bool active = reader.ReadBoolean();
                    CoopRole source = (CoopRole)reader.ReadByte();
                    if (source != CoopRole.Host && source != CoopRole.Guest) return false;
                    CoopPlacementPreviewData value = new CoopPlacementPreviewData { Active = active, Source = source };
                    ReadPlacementCore(reader, out value.Kind, out value.Sequence, out value.ParentId, out value.ParentReference, out value.TypeName,
                        out value.SizeIndex, out value.PositionX, out value.PositionY, out value.PositionZ, out value.RotationY, out value.Valid);
                    if (stream.Position != stream.Length) return false;
                    preview = value;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        public static byte[] CreatePlacementRequestPacket(CoopPlacementCommandData command)
        {
            if (command == null) throw new ArgumentNullException("command");
            if (command.SessionId == 0) throw new ArgumentException("placement session is invalid", "command");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktPlacementRequest);
                writer.Write(command.SessionId);
                WritePlacementCore(writer, command.Kind, command.RequestId, command.ParentId, command.ParentReference, command.TypeName,
                    command.SizeIndex, command.PositionX, command.PositionY, command.PositionZ, command.RotationY, command.Valid);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParsePlacementRequestPacket(byte[] packet, out CoopPlacementCommandData command)
        {
            return TryParsePlacementCommandPacket(packet, PktPlacementRequest, out command);
        }

        public static byte[] CreatePlacementResultPacket(bool accepted, long applyTick, CoopPlacementCommandData command)
        {
            if (command == null) throw new ArgumentNullException("command");
            if (command.SessionId == 0) throw new ArgumentException("placement session is invalid", "command");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktPlacementResult);
                writer.Write(accepted);
                writer.Write(applyTick);
                writer.Write(command.SessionId);
                WritePlacementCore(writer, command.Kind, command.RequestId, command.ParentId, command.ParentReference, command.TypeName,
                    command.SizeIndex, command.PositionX, command.PositionY, command.PositionZ, command.RotationY, command.Valid);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParsePlacementResultPacket(byte[] packet, out bool accepted, out long applyTick, out CoopPlacementCommandData command)
        {
            accepted = false;
            applyTick = 0;
            command = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(packet ?? new byte[0]))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != PktPlacementResult) return false;
                    accepted = reader.ReadBoolean();
                    applyTick = reader.ReadInt64();
                    CoopPlacementCommandData value = ReadPlacementCommand(reader);
                    if (stream.Position != stream.Length || value.SessionId == 0) return false;
                    command = value;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        public static byte[] CreateTimeStatePacket(bool paused, int scaleIndex, CoopTimeActor actor)
        {
            return new[] { PktTimeState, (byte)(paused ? 1 : 0), (byte)Math.Max(0, Math.Min(2, scaleIndex)), (byte)actor };
        }

        public static bool TryParseTimeStatePacket(byte[] packet, out bool paused, out int scaleIndex, out CoopTimeActor actor)
        {
            paused = false;
            scaleIndex = 0;
            actor = CoopTimeActor.System;
            if (packet == null || packet.Length != 4 || packet[0] != PktTimeState || packet[3] > (byte)CoopTimeActor.Guest) return false;
            paused = packet[1] != 0;
            scaleIndex = packet[2];
            actor = (CoopTimeActor)packet[3];
            return scaleIndex <= 2;
        }

        public static byte[] CreateTimeStateRequestPacket(byte operation)
        {
            return new[] { PktTimeStateRequest, operation };
        }

        public static bool TryParseTimeStateRequestPacket(byte[] packet, out byte operation)
        {
            operation = 0;
            if (packet == null || packet.Length != 2 || packet[0] != PktTimeStateRequest) return false;
            operation = packet[1];
            return operation <= 4;
        }

        public static byte[] CreateGameOverRequestPacket(long sessionId, long tick)
        {
            return CreateGameOverPacket(PktGameOverRequest, CoopGameOverPacketKind.Request, sessionId, tick);
        }

        public static bool TryParseGameOverRequestPacket(
            byte[] packet,
            out long sessionId,
            out long tick)
        {
            return TryParseGameOverPacket(
                packet,
                PktGameOverRequest,
                CoopGameOverPacketKind.Request,
                out sessionId,
                out tick);
        }

        public static byte[] CreateGameOverStatePacket(long sessionId, long tick)
        {
            return CreateGameOverPacket(PktGameOverState, CoopGameOverPacketKind.State, sessionId, tick);
        }

        public static bool TryParseGameOverStatePacket(
            byte[] packet,
            out long sessionId,
            out long tick)
        {
            return TryParseGameOverPacket(
                packet,
                PktGameOverState,
                CoopGameOverPacketKind.State,
                out sessionId,
                out tick);
        }

        private static byte[] CreateGameOverPacket(
            byte packetType,
            CoopGameOverPacketKind kind,
            long sessionId,
            long tick)
        {
            if (sessionId == 0) throw new ArgumentOutOfRangeException("sessionId");
            if (tick < 0 || tick > MaxWorldTick) throw new ArgumentOutOfRangeException("tick");
            byte[] packet = new byte[1 + 1 + 8 + 8];
            packet[0] = packetType;
            packet[1] = (byte)kind;
            Buffer.BlockCopy(BitConverter.GetBytes(sessionId), 0, packet, 2, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(tick), 0, packet, 10, 8);
            return packet;
        }

        private static bool TryParseGameOverPacket(
            byte[] packet,
            byte packetType,
            CoopGameOverPacketKind kind,
            out long sessionId,
            out long tick)
        {
            sessionId = 0;
            tick = 0;
            if (packet == null || packet.Length != 18 || packet[0] != packetType ||
                packet[1] != (byte)kind)
                return false;
            sessionId = BitConverter.ToInt64(packet, 2);
            tick = BitConverter.ToInt64(packet, 10);
            return sessionId != 0 && tick >= 0 && tick <= MaxWorldTick;
        }

        public static byte[] CreateSelectionPacket(CoopSelectionData selection)
        {
            if (selection == null) throw new ArgumentNullException("selection");
            if (selection.Sequence < 0) throw new ArgumentOutOfRangeException("selection.Sequence");
            CoopObjectReference primary = selection.GetPrimaryReference();
            CoopObjectReference link = selection.GetLinkReference();
            if (selection.Active && !CoopObjectReferenceValidator.IsValid(primary))
                throw new ArgumentException("selection primary reference is invalid", "selection");
            if (selection.HasLinkTarget && !CoopObjectReferenceValidator.IsValid(link))
                throw new ArgumentException("selection link reference is invalid", "selection");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktSelection);
                writer.Write((byte)(selection.Active ? 1 : 0));
                writer.Write(selection.Sequence);
                if (selection.Active) WriteObjectReference(writer, primary);
                bool hasLinkTarget = selection.Active && selection.HasLinkTarget;
                writer.Write((byte)(hasLinkTarget ? 1 : 0));
                if (hasLinkTarget)
                {
                    WriteObjectReference(writer, link);
                }
                return stream.ToArray();
            }
        }

        public static bool TryParseSelectionPacket(byte[] packet, out CoopSelectionData selection)
        {
            selection = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(packet ?? new byte[0]))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != PktSelection) return false;
                    byte activeByte = reader.ReadByte();
                    if (activeByte > 1) return false;
                    bool active = activeByte != 0;
                    long sequence = reader.ReadInt64();
                    if (sequence < 0) return false;
                    CoopObjectReference primary = active ? ReadObjectReference(reader) : null;
                    byte hasLinkTargetByte = reader.ReadByte();
                    if (hasLinkTargetByte > 1) return false;
                    bool hasLinkTarget = active && hasLinkTargetByte != 0;
                    if (!active && hasLinkTargetByte != 0) return false;
                    CoopObjectReference link = null;
                    if (hasLinkTarget)
                    {
                        link = ReadObjectReference(reader);
                    }
                    if (stream.Position != stream.Length) return false;
                    selection = new CoopSelectionData
                    {
                        Active = active,
                        Sequence = sequence,
                        ObjectId = primary == null ? 0 : primary.ObjectId,
                        TypeName = primary == null ? string.Empty : primary.TypeName,
                        PrimaryReference = primary,
                        HasLinkTarget = hasLinkTarget,
                        LinkObjectId = link == null ? 0 : link.ObjectId,
                        LinkTypeName = link == null ? string.Empty : link.TypeName,
                        LinkReference = link
                    };
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        public static byte[] CreateConnectionRequestPacket(CoopConnectionCommandData command)
        {
            ValidateConnectionCommand(command);
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktConnectionRequest);
                writer.Write(command.SessionId);
                writer.Write(command.RequestId);
                WriteObjectReference(writer, command.GetFirstModuleReference());
                WriteObjectReference(writer, command.GetSecondModuleReference());
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParseConnectionRequestPacket(byte[] packet, out CoopConnectionCommandData command)
        {
            return TryParseConnectionCommandPacket(packet, PktConnectionRequest, 1, out command);
        }

        public static byte[] CreateConnectionResultPacket(bool accepted, long applyTick, CoopConnectionCommandData command)
        {
            ValidateConnectionCommand(command);
            if (applyTick < 0) throw new ArgumentOutOfRangeException("applyTick");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktConnectionResult);
                writer.Write((byte)(accepted ? 1 : 0));
                writer.Write(applyTick);
                writer.Write(command.SessionId);
                writer.Write(command.RequestId);
                WriteObjectReference(writer, command.GetFirstModuleReference());
                WriteObjectReference(writer, command.GetSecondModuleReference());
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParseConnectionResultPacket(byte[] packet, out bool accepted, out long applyTick, out CoopConnectionCommandData command)
        {
            accepted = false;
            applyTick = 0;
            command = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(packet ?? new byte[0]))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != PktConnectionResult) return false;
                    byte acceptedByte = reader.ReadByte();
                    if (acceptedByte > 1) return false;
                    accepted = acceptedByte != 0;
                    applyTick = reader.ReadInt64();
                    if (applyTick < 0) return false;
                    CoopConnectionCommandData value = ReadConnectionCommand(reader);
                    if (stream.Position != stream.Length || !IsValidConnectionCommand(value)) return false;
                    command = value;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        public static byte[] CreateRecycleRequestPacket(CoopRecycleCommandData command)
        {
            ValidateRecycleCommand(command);
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktRecycleRequest);
                WriteRecycleCommand(writer, command);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParseRecycleRequestPacket(byte[] packet, out CoopRecycleCommandData command)
        {
            return TryParseRecycleCommandPacket(packet, PktRecycleRequest, 1, out command);
        }

        public static byte[] CreateRecycleResultPacket(bool accepted, long applyTick, CoopRecycleCommandData command)
        {
            ValidateRecycleCommand(command);
            if (applyTick < 0) throw new ArgumentOutOfRangeException("applyTick");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PktRecycleResult);
                writer.Write((byte)(accepted ? 1 : 0));
                writer.Write(applyTick);
                WriteRecycleCommand(writer, command);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParseRecycleResultPacket(byte[] packet, out bool accepted, out long applyTick, out CoopRecycleCommandData command)
        {
            accepted = false;
            applyTick = 0;
            command = null;
            if (packet == null || packet.Length < 26 || packet[0] != PktRecycleResult || packet[1] > 1) return false;
            accepted = packet[1] != 0;
            applyTick = BitConverter.ToInt64(packet, 2);
            if (applyTick < 0) return false;
            return TryParseRecycleCommandPacket(packet, PktRecycleResult, 10, out command);
        }

        private static bool TryParseRecycleCommandPacket(byte[] packet, byte packetType, int offset, out CoopRecycleCommandData command)
        {
            command = null;
            try
            {
                if (packet == null || packet.Length < offset + 15 || packet[0] != packetType) return false;
                using (MemoryStream stream = new MemoryStream(packet))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    stream.Position = offset;
                    CoopRecycleCommandData value = ReadRecycleCommand(reader);
                    if (stream.Position != stream.Length || !IsValidRecycleCommand(value)) return false;
                    command = value;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static void WriteRecycleCommand(
            BinaryWriter writer,
            CoopRecycleCommandData command)
        {
            CoopObjectReference target = command.GetTargetReference();
            byte[] typeBytes = Encoding.ASCII.GetBytes(target.TypeName);
            writer.Write(command.SessionId);
            writer.Write(command.RequestId);
            writer.Write(target.ObjectId);
            writer.Write((ushort)typeBytes.Length);
            writer.Write(typeBytes);
            writer.Write((byte)(target.HasPosition ? 1 : 0));
            if (!target.HasPosition) return;
            writer.Write(target.PositionX);
            writer.Write(target.PositionY);
            writer.Write(target.PositionZ);
        }

        private static CoopRecycleCommandData ReadRecycleCommand(BinaryReader reader)
        {
            long sessionId = reader.ReadInt64();
            long requestId = reader.ReadInt64();
            int objectId = reader.ReadInt32();
            ushort typeLength = reader.ReadUInt16();
            if (typeLength == 0 || typeLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                throw new InvalidDataException("recycle target type length is invalid");
            byte[] typeBytes = reader.ReadBytes(typeLength);
            if (typeBytes.Length != typeLength) throw new EndOfStreamException();
            string typeName = Encoding.ASCII.GetString(typeBytes);
            byte positionByte = reader.ReadByte();
            if (positionByte > 1) throw new InvalidDataException("recycle target position flag is invalid");
            bool hasPosition = positionByte != 0;
            float x = 0f;
            float y = 0f;
            float z = 0f;
            if (hasPosition)
            {
                x = reader.ReadSingle();
                y = reader.ReadSingle();
                z = reader.ReadSingle();
            }
            CoopObjectReference target = new CoopObjectReference(
                objectId, typeName, x, y, z, hasPosition);
            return new CoopRecycleCommandData
            {
                SessionId = sessionId,
                RequestId = requestId,
                ObjectId = objectId,
                TypeName = typeName,
                TargetReference = target
            };
        }

        private static void ValidateRecycleCommand(CoopRecycleCommandData command)
        {
            if (!IsValidRecycleCommand(command)) throw new ArgumentException("invalid recycle command");
        }

        private static bool IsValidRecycleCommand(CoopRecycleCommandData command)
        {
            if (command == null || command.SessionId == 0 || command.RequestId <= 0) return false;
            CoopObjectReference target = command.GetTargetReference();
            if (target == null || target.ObjectId != command.ObjectId ||
                !StringComparer.Ordinal.Equals(target.TypeName, command.TypeName) ||
                !CoopRecyclePolicy.IsValidObjectId(target.ObjectId, target.TypeName) ||
                !CoopObjectReferenceValidator.IsSafeTypeName(target.TypeName)) return false;
            if (!target.HasPosition) return true;
            return CoopObjectReferenceValidator.IsValid(new CoopObjectReference(
                target.ObjectId <= 0 ? 1 : target.ObjectId,
                target.TypeName,
                target.PositionX,
                target.PositionY,
                target.PositionZ,
                true));
        }

        private static bool TryParseConnectionCommandPacket(byte[] packet, byte packetType, int offset, out CoopConnectionCommandData command)
        {
            command = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(packet ?? new byte[0]))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (offset != 1 || reader.ReadByte() != packetType) return false;
                    CoopConnectionCommandData value = ReadConnectionCommand(reader);
                    if (stream.Position != stream.Length || !IsValidConnectionCommand(value)) return false;
                    command = value;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static void ValidateConnectionCommand(CoopConnectionCommandData command)
        {
            if (!IsValidConnectionCommand(command)) throw new ArgumentException("invalid connection command");
        }

        private static bool IsValidConnectionCommand(CoopConnectionCommandData command)
        {
            if (command == null || command.SessionId == 0 || command.RequestId <= 0 ||
                command.FirstModuleId <= 0 || command.SecondModuleId <= 0 ||
                command.FirstModuleId == command.SecondModuleId) return false;
            return CoopObjectReferenceValidator.IsValid(command.GetFirstModuleReference()) &&
                CoopObjectReferenceValidator.IsValid(command.GetSecondModuleReference());
        }

        private static CoopConnectionCommandData ReadConnectionCommand(BinaryReader reader)
        {
            long sessionId = reader.ReadInt64();
            long requestId = reader.ReadInt64();
            CoopObjectReference first = ReadObjectReference(reader);
            CoopObjectReference second = ReadObjectReference(reader);
            return new CoopConnectionCommandData
            {
                SessionId = sessionId,
                RequestId = requestId,
                FirstModule = first,
                SecondModule = second,
                FirstModuleId = first.ObjectId,
                SecondModuleId = second.ObjectId
            };
        }

        private static void WriteObjectReference(BinaryWriter writer, CoopObjectReference reference)
        {
            if (!CoopObjectReferenceValidator.IsValid(reference))
                throw new ArgumentException("invalid object reference", "reference");
            byte[] typeBytes = Encoding.ASCII.GetBytes(reference.TypeName);
            writer.Write(reference.ObjectId);
            writer.Write((ushort)typeBytes.Length);
            writer.Write(typeBytes);
            writer.Write((byte)(reference.HasPosition ? 1 : 0));
            if (!reference.HasPosition) return;
            writer.Write(reference.PositionX);
            writer.Write(reference.PositionY);
            writer.Write(reference.PositionZ);
        }

        private static CoopObjectReference ReadObjectReference(BinaryReader reader)
        {
            int objectId = reader.ReadInt32();
            ushort typeLength = reader.ReadUInt16();
            if (typeLength == 0 || typeLength > CoopObjectReferenceValidator.MaxTypeNameChars)
                throw new InvalidDataException("object type name length is invalid");
            byte[] typeBytes = reader.ReadBytes(typeLength);
            if (typeBytes.Length != typeLength) throw new EndOfStreamException();
            byte positionByte = reader.ReadByte();
            if (positionByte > 1) throw new InvalidDataException("object position flag is invalid");
            bool hasPosition = positionByte != 0;
            float x = 0f;
            float y = 0f;
            float z = 0f;
            if (hasPosition)
            {
                x = reader.ReadSingle();
                y = reader.ReadSingle();
                z = reader.ReadSingle();
            }
            CoopObjectReference reference = new CoopObjectReference(
                objectId,
                Encoding.ASCII.GetString(typeBytes),
                x,
                y,
                z,
                hasPosition);
            if (!CoopObjectReferenceValidator.IsValid(reference))
                throw new InvalidDataException("object reference is invalid");
            return reference;
        }

        private static bool TryParsePlacementCommandPacket(byte[] packet, byte expectedType, out CoopPlacementCommandData command)
        {
            command = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(packet ?? new byte[0]))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != expectedType) return false;
                    command = ReadPlacementCommand(reader);
                    return stream.Position == stream.Length && command.SessionId != 0;
                }
            }
            catch (Exception) { return false; }
        }

        private static CoopPlacementCommandData ReadPlacementCommand(BinaryReader reader)
        {
            CoopPlacementCommandData value = new CoopPlacementCommandData();
            value.SessionId = reader.ReadInt64();
            ReadPlacementCore(reader, out value.Kind, out value.RequestId, out value.ParentId, out value.ParentReference, out value.TypeName,
                out value.SizeIndex, out value.PositionX, out value.PositionY, out value.PositionZ, out value.RotationY, out value.Valid);
            return value;
        }

        private static void WritePlacementCore(BinaryWriter writer, CoopPlacementKind kind, long sequence, long parentId,
            CoopObjectReference parentReference,
            string typeName, int sizeIndex, float positionX, float positionY, float positionZ, float rotationY, bool valid)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(typeName ?? string.Empty);
            if (typeBytes.Length > CoopPlacementValidator.MaxTypeNameChars) throw new ArgumentException("type name too long");
            writer.Write((byte)kind);
            writer.Write(sequence);
            writer.Write(parentId);
            writer.Write(sizeIndex);
            writer.Write(positionX);
            writer.Write(positionY);
            writer.Write(positionZ);
            writer.Write(rotationY);
            writer.Write(valid);
            writer.Write((byte)(parentReference == null ? 0 : 1));
            if (parentReference != null) WriteObjectReference(writer, parentReference);
            writer.Write((ushort)typeBytes.Length);
            writer.Write(typeBytes);
        }

        private static void ReadPlacementCore(BinaryReader reader, out CoopPlacementKind kind, out long sequence, out long parentId,
            out CoopObjectReference parentReference,
            out string typeName, out int sizeIndex, out float positionX, out float positionY, out float positionZ, out float rotationY, out bool valid)
        {
            kind = (CoopPlacementKind)reader.ReadByte();
            sequence = reader.ReadInt64();
            parentId = reader.ReadInt64();
            sizeIndex = reader.ReadInt32();
            positionX = reader.ReadSingle();
            positionY = reader.ReadSingle();
            positionZ = reader.ReadSingle();
            rotationY = reader.ReadSingle();
            valid = reader.ReadBoolean();
            byte parentReferenceByte = reader.ReadByte();
            if (parentReferenceByte > 1) throw new InvalidDataException("parent reference flag is invalid");
            parentReference = parentReferenceByte == 0 ? null : ReadObjectReference(reader);
            ushort typeLength = reader.ReadUInt16();
            if (typeLength > CoopPlacementValidator.MaxTypeNameChars) throw new InvalidDataException("type name too long");
            byte[] typeBytes = reader.ReadBytes(typeLength);
            if (typeBytes.Length != typeLength) throw new EndOfStreamException();
            typeName = Encoding.ASCII.GetString(typeBytes);
        }
    }

    public static class CoopInputPolicy
    {
        public static bool IsInputAllowed(bool fullMode, bool applicationFocused)
        {
            return !fullMode || applicationFocused;
        }
    }

    public static class CoopApplicationFocusPolicy
    {
        public static bool Resolve(
            bool applicationFocused,
            bool callbackReceived,
            bool callbackFocused)
        {
            // Unity's property or callback can lag by one frame around
            // Alt+Tab. Either positive signal is enough to recover input;
            // both must report background before input is blocked.
            return applicationFocused || (callbackReceived && callbackFocused);
        }
    }

    public static class CoopCameraUpdatePolicy
    {
        public static bool ShouldRun(bool fullMode, bool applicationFocused, bool cinematicActive)
        {
            return !fullMode || applicationFocused || cinematicActive;
        }
    }

    public static class CoopIntroCinematicPolicy
    {
        public static bool ShouldRun(
            bool fullMode,
            bool shipFieldAvailable,
            bool shipPresent,
            bool shipDestroyed)
        {
            if (!fullMode || !shipFieldAvailable) return true;
            return shipPresent && !shipDestroyed;
        }
    }

    public static class CoopHostBootstrapPolicy
    {
        public static bool CanPublishInitialSnapshot(
            bool isHost,
            bool worldLoaded,
            bool isFreshWorld,
            bool colonyShipPresent,
            bool colonyShipDone,
            int characterCount,
            bool cinematicActive,
            bool snapshotPublished)
        {
            if (!isHost || !worldLoaded || snapshotPublished) return false;
            if (!isFreshWorld) return true;
            if (!colonyShipPresent) return false;
            // Freeze an in-flight ship so both peers run the vanilla landing
            // cinematic from the same serialized position.
            if (!colonyShipDone) return characterCount == 0 && cinematicActive;
            return characterCount > 0 && !cinematicActive;
        }

        public static bool ShouldAdvanceLocalSimulation(
            bool isHost,
            bool bootstrapStarted,
            bool worldStable,
            bool sharedSessionStarted)
        {
            if (!isHost || !bootstrapStarted) return false;
            return !worldStable || sharedSessionStarted;
        }

        public static bool ShouldFreezeStableWorld(
            bool isHost,
            bool bootstrapStarted,
            bool worldStable,
            bool sharedSessionStarted)
        {
            return isHost && bootstrapStarted && worldStable && !sharedSessionStarted;
        }

        public static bool CanTransmitInitialSnapshot(
            bool worldStable,
            bool guestConnected,
            bool handshakeComplete,
            bool snapshotPublished)
        {
            return worldStable && guestConnected && handshakeComplete && !snapshotPublished;
        }
    }

    public static class CoopGuestBootstrapPolicy
    {
        public static bool ShouldWaitForHostWorld(CoopRole role, bool hostSnapshotReceived)
        {
            return role == CoopRole.Guest && !hostSnapshotReceived;
        }
    }

    public static class CoopSnapshotVerificationPolicy
    {
        public static bool ShouldRemainPendingOnGameLoaded(
            CoopRole role,
            bool localWorldReady,
            CoopStartupPhase phase)
        {
            return role == CoopRole.Guest &&
                (!localWorldReady || phase != CoopStartupPhase.ReadyToStart);
        }
    }

    public static class CoopStartAuthorizationPolicy
    {
        public static bool CanApplyAuthorizedStart(
            CoopRole role,
            CoopStartupPhase phase)
        {
            return role == CoopRole.Guest
                ? phase == CoopStartupPhase.ReadyToStart
                : phase == CoopStartupPhase.ReadyToStart ||
                    phase == CoopStartupPhase.Running;
        }
    }

    public static class CoopSeedArgument
    {
        public static bool TryParse(string value, out int? fixedSeed)
        {
            fixedSeed = null;
            if (StringComparer.OrdinalIgnoreCase.Equals(value, "random")) return true;
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < 0) return false;
            fixedSeed = parsed;
            return true;
        }
    }

    public sealed class CoopSessionSeedSelection
    {
        private CoopSessionSeedSelection(bool hasSeed, int seed)
        {
            HasSeed = hasSeed;
            Seed = seed;
        }

        public bool HasSeed { get; private set; }
        public int Seed { get; private set; }

        public static CoopSessionSeedSelection Create(
            CoopRole role,
            bool newGameRequested,
            int? fixedSeed,
            Func<int> randomSeedFactory)
        {
            if (role != CoopRole.Host || !newGameRequested)
                return new CoopSessionSeedSelection(false, -1);
            if (fixedSeed.HasValue)
                return new CoopSessionSeedSelection(true, fixedSeed.Value);
            if (randomSeedFactory == null) throw new ArgumentNullException("randomSeedFactory");
            return new CoopSessionSeedSelection(true, randomSeedFactory());
        }
    }

    public static class CoopFixedUpdatePolicy
    {
        public static bool ShouldSkipSimulation(bool fullMode, bool worldTransferPending, bool shouldFreeze)
        {
            return fullMode && !worldTransferPending && shouldFreeze;
        }

        public static bool ShouldRunGameStateUpdate(bool fullMode, bool worldTransferPending, bool allowSimulation)
        {
            return !fullMode || (!worldTransferPending && allowSimulation);
        }

        public static bool ShouldRunGameStateUpdate(
            bool fullMode,
            bool coordinatorAvailable,
            bool worldTransferPending,
            bool allowSimulation)
        {
            return !fullMode || (coordinatorAvailable &&
                !worldTransferPending && allowSimulation);
        }

        public static bool ShouldRunVanilla(bool waitingForStart, bool shouldBlockVanillaStart, bool worldTransferPending)
        {
            if (worldTransferPending && !waitingForStart)
                return true;
            return !waitingForStart || !shouldBlockVanillaStart;
        }
    }

    public static class CoopWorldTransferPolicy
    {
        public static bool IsPending(
            bool loadQueued,
            bool recoveryPending,
            bool hostInitialWorldLoading,
            bool snapshotVerificationPending = false)
        {
            return loadQueued || recoveryPending || hostInitialWorldLoading || snapshotVerificationPending;
        }
    }

    public static class CoopTickProgressPolicy
    {
        public static long NextTick(long currentTick, bool simulationAdvanced)
        {
            if (currentTick < 0) throw new ArgumentOutOfRangeException(nameof(currentTick));
            return simulationAdvanced ? checked(currentTick + 1) : currentTick;
        }
    }

    public static class CoopTickLeadPolicy
    {
        // Peer tick beacon'lari kare basina bir kez gelir; ~30 FPS'te bu tek
        // basina 1-2 tick bayatlik demektir. 2 tick'lik pencere localhost'ta
        // bile misafiri zamanin ~%30'unda durduruyordu (canli soak olcumu).
        private const int BaseLeadTicks = 4;
        private const int MaxStaticLeadTicks = 16;

        public static int MaxLeadTicks(int scaleIndex)
        {
            if (scaleIndex < 0 || scaleIndex > 2)
                throw new ArgumentOutOfRangeException("scaleIndex");
            // FixedUpdate frequency scales with 1x/2x/4x. Increase the tick
            // allowance with speed, but stay below the centralized dynamic
            // command lead after accounting for the gate's next tick.
            return Math.Min(MaxStaticLeadTicks, BaseLeadTicks << scaleIndex);
        }

        public static int MaxLeadTicks(int scaleIndex, int networkBudgetMilliseconds)
        {
            if (scaleIndex < 0 || scaleIndex > 2)
                throw new ArgumentOutOfRangeException("scaleIndex");
            if (networkBudgetMilliseconds < 0)
                throw new ArgumentOutOfRangeException("networkBudgetMilliseconds");

            int commandLead = CoopCommandLeadPolicy.ApplyLeadTicks(
                scaleIndex, networkBudgetMilliseconds);
            int ticksPerSecond = 50 << scaleIndex;
            int oneWayTicks = (networkBudgetMilliseconds * ticksPerSecond + 1999) / 2000;
            int latencyLead = oneWayTicks + 2;
            return Math.Min(commandLead - 6,
                Math.Max(MaxLeadTicks(scaleIndex), latencyLead));
        }
    }

    public static class CoopTickGatePolicy
    {
        public static int ResumeLeadTicks(int maxLeadTicks)
        {
            if (maxLeadTicks < 0)
                throw new ArgumentOutOfRangeException("maxLeadTicks");
            int hysteresis = Math.Max(2, maxLeadTicks / 5);
            return Math.Max(0, maxLeadTicks - hysteresis);
        }

        public static bool ShouldFreeze(
            long localTick,
            long peerTick,
            int maxLeadTicks,
            bool currentlyFrozen,
            bool peerSampleReady)
        {
            if (maxLeadTicks < 0)
                throw new ArgumentOutOfRangeException("maxLeadTicks");
            if (!peerSampleReady || localTick < 0 || peerTick < 0) return false;

            long lead = localTick - peerTick;
            if (lead <= 0) return false;
            return currentlyFrozen
                ? lead > ResumeLeadTicks(maxLeadTicks)
                : lead > maxLeadTicks;
        }
    }

    public static class CoopCommandLeadPolicy
    {
        // ~240 ms her hizda; genis tick kapisi icin 6 tick teslim marji birakir.
        private const int BaseLeadTicks = 12;

        public static int ApplyLeadTicks(int scaleIndex)
        {
            if (scaleIndex < 0 || scaleIndex > 2)
                throw new ArgumentOutOfRangeException("scaleIndex");
            // 12/24/48 ticks all represent roughly the same real-time delivery
            // window at Planetbase's 1x/2x/4x FixedUpdate rates.
            return BaseLeadTicks << scaleIndex;
        }

        public static int ApplyLeadTicks(int scaleIndex, int networkBudgetMilliseconds)
        {
            if (scaleIndex < 0 || scaleIndex > 2)
                throw new ArgumentOutOfRangeException("scaleIndex");
            if (networkBudgetMilliseconds < 0)
                throw new ArgumentOutOfRangeException("networkBudgetMilliseconds");

            int ticksPerSecond = 50 << scaleIndex;
            int roundTripTicks = (networkBudgetMilliseconds * ticksPerSecond + 999) / 1000;
            int latencyLead = roundTripTicks + 4;
            return Math.Min((int)CoopProtocol.MaxCommandLeadTicks,
                Math.Max(ApplyLeadTicks(scaleIndex), latencyLead));
        }
    }

    public sealed class CoopLatencyEstimator
    {
        private const int MaxAcceptedSampleMilliseconds = 5000;
        private const int MaxBudgetMilliseconds = 600;
        private const int SampleWindowSize = 9;
        private readonly int[] mSamples = new int[SampleWindowSize];
        private int mSampleCount;
        private int mSampleCursor;
        private int mSmoothedRttMilliseconds;
        private int mJitterMilliseconds;
        private int mBudgetMilliseconds;
        private bool mHasSample;

        public int SmoothedRttMilliseconds
        {
            get { return mSmoothedRttMilliseconds; }
        }

        public int JitterMilliseconds
        {
            get { return mJitterMilliseconds; }
        }

        public int BudgetMilliseconds
        {
            get { return mBudgetMilliseconds; }
        }

        public bool HasSample
        {
            get { return mHasSample; }
        }

        public bool RecordSample(int milliseconds)
        {
            if (milliseconds <= 0 || milliseconds > MaxAcceptedSampleMilliseconds)
                return false;
            if (!mHasSample)
            {
                mHasSample = true;
                mSmoothedRttMilliseconds = milliseconds;
                mJitterMilliseconds = 0;
                mBudgetMilliseconds = Math.Min(
                    MaxBudgetMilliseconds, milliseconds);
                mSamples[0] = milliseconds;
                mSampleCount = 1;
                mSampleCursor = 1;
                return true;
            }

            // The heartbeat includes Unity/main-thread queueing, so one long
            // render frame is not evidence that the route itself became slow.
            // A rolling median (p50) rejects that outlier; median absolute
            // deviation tracks stable jitter. Two sustained high samples are
            // enough to move the median and expose a genuinely slow route.
            mSamples[mSampleCursor] = milliseconds;
            mSampleCursor = (mSampleCursor + 1) % SampleWindowSize;
            if (mSampleCount < SampleWindowSize) mSampleCount++;

            int[] ordered = new int[mSampleCount];
            Array.Copy(mSamples, ordered, mSampleCount);
            Array.Sort(ordered);
            int median = ordered[(ordered.Length - 1) / 2];
            int[] deviations = new int[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
                deviations[i] = Math.Abs(ordered[i] - median);
            Array.Sort(deviations);
            int deviation = deviations[(deviations.Length - 1) / 2];

            mSmoothedRttMilliseconds = median;
            mJitterMilliseconds = deviation;
            int stableBudget = median + deviation * 3;
            int burstBudget = milliseconds > median
                ? Math.Min(milliseconds, median + 200)
                : median;
            mBudgetMilliseconds = Math.Min(MaxBudgetMilliseconds,
                Math.Max(stableBudget, burstBudget));
            return true;
        }

        public void Reset()
        {
            mHasSample = false;
            mSmoothedRttMilliseconds = 0;
            mJitterMilliseconds = 0;
            mBudgetMilliseconds = 0;
            mSampleCount = 0;
            mSampleCursor = 0;
            Array.Clear(mSamples, 0, mSamples.Length);
        }
    }

    public static class CoopNetworkTiming
    {
        private static int sBudgetMilliseconds;

        public static int CurrentBudgetMilliseconds
        {
            get { return Volatile.Read(ref sBudgetMilliseconds); }
        }

        public static void PublishBudget(int milliseconds)
        {
            if (milliseconds < 0) milliseconds = 0;
            Volatile.Write(ref sBudgetMilliseconds, Math.Min(600, milliseconds));
        }

        public static void Reset()
        {
            Volatile.Write(ref sBudgetMilliseconds, 0);
        }
    }

    public static class CoopNetworkQuality
    {
        public const int HighLatencyMilliseconds = 180;
        public const int CriticalLatencyMilliseconds = 350;

        public static bool IsHighLatency(int pingMilliseconds)
        {
            return pingMilliseconds >= HighLatencyMilliseconds;
        }

        public static bool IsCriticalLatency(int pingMilliseconds)
        {
            return pingMilliseconds >= CriticalLatencyMilliseconds;
        }
    }

    public static class CoopRecoveryPolicy
    {
        public static bool ShouldCleanupParticlesBeforeWorldClear(bool fullMode)
        {
            // ParticleManager.clear is itself the world-teardown boundary.
            // Cleanup must also run on disconnect/error exits where the
            // snapshot-transfer flags have already been reset.
            return fullMode;
        }

        public static bool ShouldRunGameManagerFixedUpdate(
            bool isHost,
            bool recoveryPending,
            bool snapshotVerificationPending = false,
            bool localSnapshotLoadPending = false)
        {
            if (isHost)
            {
                // During recovery the old simulation remains frozen, but the
                // staged GameManager loader must keep advancing until the
                // replacement checkpoint reaches Waiting.
                return !recoveryPending ||
                    (localSnapshotLoadPending && !snapshotVerificationPending);
            }
            return !snapshotVerificationPending;
        }

        public static bool ShouldPrepareWorldTransfer(bool isGuest, bool worldTransferPending)
        {
            return isGuest && worldTransferPending;
        }

        public static bool ShouldCheckStartupTimeout(
            bool startApplied,
            bool recoveryPending,
            bool startupFailed)
        {
            return !startupFailed && (!startApplied || recoveryPending);
        }

        public static bool CanAcceptIncomingWorld(
            bool firstSnapshot,
            bool sessionRunning,
            bool worldLoaded,
            bool startApplied,
            bool activeGameState,
            bool sessionTerminal,
            long activeSessionId,
            long incomingSessionId)
        {
            if (incomingSessionId == 0) return false;
            if (firstSnapshot) return true;
            return sessionRunning && worldLoaded && startApplied &&
                activeGameState && !sessionTerminal && activeSessionId != 0 &&
                incomingSessionId != activeSessionId;
        }
    }

    public static class CoopAuthorizedStartTimePolicy
    {
        public static bool ShouldRestore(CoopRole role, bool recoveryPending)
        {
            return role == CoopRole.Guest ||
                (role == CoopRole.Host && recoveryPending);
        }
    }

    /// <summary>
    /// Couples the network START transmission with arming the host's matching
    /// main-thread transition. Keeping these two effects together prevents an
    /// early guest READY from leaving the host frozen after recovery reload.
    /// </summary>
    public static class CoopHostStartDispatch
    {
        public static bool TrySend(
            byte[] startPacket,
            Func<byte[], bool> sender,
            out bool localStartPending)
        {
            localStartPending = false;
            if (startPacket == null) return true;
            if (sender == null) throw new ArgumentNullException("sender");
            if (!sender(startPacket)) return false;

            localStartPending = true;
            return true;
        }
    }

    /// <summary>
    /// Recovery always reloads and verifies the authoritative checkpoint on
    /// both peers. The loader gate is intentionally separate from simulation
    /// gating so GameManager can finish its staged load while GameStateGame
    /// remains blocked from advancing the old world.
    /// </summary>
    public static class CoopRecoveryReloadPolicy
    {
        public static bool RequiresLocalReload(CoopRole role, bool recoveryPending)
        {
            return recoveryPending &&
                (role == CoopRole.Host || role == CoopRole.Guest);
        }

        public static bool ShouldRunGameManagerFixedUpdate(
            bool isHost,
            bool recoveryPending,
            bool localSnapshotLoadPending,
            bool snapshotVerificationPending)
        {
            if (!recoveryPending)
                return !snapshotVerificationPending;
            return localSnapshotLoadPending && !snapshotVerificationPending;
        }

        public static bool CanAuthorizeRecoveryStart(
            bool hostLocalReady,
            bool guestReady,
            bool startAlreadyAuthorized)
        {
            return !startAlreadyAuthorized && hostLocalReady && guestReady;
        }

        public static string GetCheckpointFileName(CoopRole role)
        {
            return "coop_recv.sav";
        }
    }

    public static class CoopColonyShipPolicy
    {
        public static bool ShouldCompleteExhaustedSpawnUpdate(
            int state,
            bool hasSpawnList,
            int spawnIndex,
            int spawnCount,
            float spawnTimer)
        {
            const int SpawnState = 3;
            const float VanillaCompletionWindow = 0.5f;
            bool exhausted = !hasSpawnList || spawnIndex < 0 || spawnIndex >= spawnCount;
            return state == SpawnState && exhausted && spawnTimer < VanillaCompletionWindow;
        }

        public static int ClampSpawnIndex(int spawnIndex, int spawnCount)
        {
            if (spawnCount < 0) throw new ArgumentOutOfRangeException(nameof(spawnCount));
            if (spawnIndex < 0) return 0;
            return spawnIndex > spawnCount ? spawnCount : spawnIndex;
        }
    }

    public static class CoopRecyclePolicy
    {
        public static bool IsValidObjectId(int objectId, string typeName)
        {
            if (StringComparer.Ordinal.Equals(typeName, "ColonyShip")) return objectId >= 0;
            return objectId > 0;
        }
    }

    public static class CoopAuthoritativeMutationPolicy
    {
        public static bool CanRunDirectly(
            bool fullMode,
            bool coordinatorAvailable,
            bool applyingAuthoritative)
        {
            return !fullMode || (coordinatorAvailable && applyingAuthoritative);
        }
    }

    public static class CoopRuntimeSubsystemPolicy
    {
        public static bool RequiresPlacementCoordinator(
            bool fullMode,
            bool coordinatorAvailable)
        {
            return fullMode && !coordinatorAvailable;
        }

        public static bool RequiresBackgroundProcessing(
            bool fullMode,
            bool runInBackground)
        {
            return fullMode && !runInBackground;
        }
    }

    public static class CoopAuthoritativeIdPolicy
    {
        public static bool IsExpectedAdvance(int previousNextId, int entityId, int nextId)
        {
            return previousNextId >= 0 && previousNextId < int.MaxValue &&
                entityId == previousNextId && nextId == previousNextId + 1;
        }
    }

    public struct CoopDisasterState
    {
        public bool InProgress;
        public float TimeToNext;
        public float Time;
        public float Duration;
    }

    public static class CoopDisasterSnapshot
    {
        public static bool TryRead(XmlNode root, string disasterName, out CoopDisasterState state)
        {
            state = default(CoopDisasterState);
            if (root == null || string.IsNullOrEmpty(disasterName)) return false;
            XmlNode node = root[disasterName];
            if (node == null) return false;

            string prefix = disasterName;
            return TryReadBool(node[prefix + "-in-progress"], out state.InProgress) &&
                TryReadFloat(node["time-to-next-" + prefix], out state.TimeToNext) &&
                TryReadFloat(node["time"], out state.Time) &&
                TryReadFloat(node[prefix + "-time"], out state.Duration);
        }

        private static bool TryReadBool(XmlNode node, out bool value)
        {
            value = false;
            string text = node?.Attributes?["value"]?.Value;
            return bool.TryParse(text, out value);
        }

        private static bool TryReadFloat(XmlNode node, out float value)
        {
            value = 0f;
            string text = node?.Attributes?["value"]?.Value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// Selection packet identity is independent of the order in which the
    /// game's two-module link pair is serialized. The selected module remains
    /// the primary module, while the link target is the other endpoint.
    /// </summary>
    public static class CoopSelectionIdentity
    {
        public static bool Same(CoopSelectionData left, CoopSelectionData right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.Active != right.Active) return false;
            if (!left.Active) return true;
            if (left.ObjectId == right.ObjectId &&
                StringComparer.Ordinal.Equals(left.TypeName, right.TypeName) &&
                left.HasLinkTarget == right.HasLinkTarget &&
                left.LinkObjectId == right.LinkObjectId &&
                StringComparer.Ordinal.Equals(left.LinkTypeName, right.LinkTypeName))
            {
                return true;
            }

            if (!left.HasLinkTarget || !right.HasLinkTarget) return false;
            return left.ObjectId == right.LinkObjectId &&
                StringComparer.Ordinal.Equals(left.TypeName, right.LinkTypeName) &&
                left.LinkObjectId == right.ObjectId &&
                StringComparer.Ordinal.Equals(left.LinkTypeName, right.TypeName);
        }
    }

    public static class CoopTimeStatePolicy
    {
        public static bool ShouldApply(bool worldLoaded, bool hasPendingState)
        {
            return worldLoaded && hasPendingState;
        }
    }

    public static class CoopPlacementRequestIds
    {
        public static long For(CoopRole role, long sequence)
        {
            if (sequence <= 0 || sequence > (long.MaxValue / 2))
                throw new ArgumentOutOfRangeException("sequence");

            long value = sequence * 2;
            return role == CoopRole.Guest ? value + 1 : value;
        }
    }

    public static class CoopPlacementPreviewLifecycle
    {
        public static bool IsLocalInitiatedRequest(
            CoopRole localRole,
            long commandRequestId,
            long awaitingLocalRequestId)
        {
            return awaitingLocalRequestId > 0 &&
                commandRequestId == awaitingLocalRequestId &&
                CoopSequencePolicy.IsFromRole(commandRequestId, localRole);
        }

        public static long PendingAfterAccepted(
            CoopRole localRole,
            long commandRequestId,
            long awaitingLocalRequestId)
        {
            return IsLocalInitiatedRequest(
                localRole, commandRequestId, awaitingLocalRequestId)
                ? commandRequestId
                : 0;
        }

        public static bool ShouldFreeze(
            long awaitingLocalRequestId,
            long pendingLocalRequestId)
        {
            return awaitingLocalRequestId > 0 || pendingLocalRequestId > 0;
        }
    }

    public static class CoopGridBalancePolicy
    {
        // Vanilla'nin 2 saniyelik yeniden hesap araligi, 50 tick/sn.
        public const int IntervalTicks = 100;

        public static bool ShouldRecalculate(long tick)
        {
            return tick % IntervalTicks == 0;
        }
    }

    public static class CoopDeterministicSeeds
    {
        public const int MeteorManager = unchecked((int)0x4D45544F);
        public const int ThunderstormManager = unchecked((int)0x5448554E);
        public const int NameGenerator = unchecked((int)0x4E414D45);
        public const int InteractionWork = unchecked((int)0x574F524B);
        public const int Wind = unchecked((int)0x57494E44);

        /// <summary>
        /// Oturum icinde sonradan olusan nesneler (or. yeni InteractionWork)
        /// icin: iki peer ayni lockstep sirasinda ayni sira numarasini uretir,
        /// boylece vanilla'nin saat tabanli System.Random'u yerine ayni seed alinir.
        /// </summary>
        public static int ForSequence(int streamSeed, long sequence)
        {
            unchecked
            {
                ulong mixed = (ulong)sequence * 0x9E3779B97F4A7C15UL;
                return ForStream(streamSeed ^ (int)(mixed >> 32) ^ (int)mixed);
            }
        }

        public static int ForStream(int streamSeed)
        {
            unchecked
            {
                uint value = (uint)streamSeed ^ 0x5EED1234u;
                value ^= value >> 16;
                value *= 16777619u;
                value ^= value >> 13;
                return (int)value;
            }
        }
    }

    public static class CoopWorldHash
    {
        // Environment and weather are sampled from each Unity instance's
        // floating-point clock. They are presentation-level state and are not
        // allowed to turn a numerically equivalent colony into a false OOS.
        private static readonly string[] LocalOnlyNodes =
            { "camera", "stats", "game-hints", "meteor-manager", "thunderstorm-manager" };
        private static readonly string[] TolerantTimerNodes = { "sandstorm", "blizzard", "solar-flare", "visitor-events" };
        // Human and ship names are generated by each Unity instance's local
        // System.Random stream. They are presentation labels, not simulation
        // state, and must not trigger a false OOS recovery.
        private static readonly string[] PresentationNameParents = { "characters", "ships" };
        private static readonly string[] OrderInsensitiveEntitySections =
            { "constructions", "characters", "resources", "ships" };
        // Character needs are continuously integrated floats. Two Unity
        // instances can legitimately serialize a one-bit rounding difference
        // even when they have applied the same simulation tick.
        private static readonly string[] ContinuousNeedNodes =
            { "Hydration", "Nutrition", "Oxygen", "Sleep" };
        // Character transforms can differ by a few thousandths between Unity
        // instances. A tenth-unit bucket absorbs that float noise while still
        // making meaningful world movement visible to the checksum.
        private const int SpatialDecimals = 1;
        private const int TimerDecimals = 1;
        private const int ContinuousNeedDecimals = 5;

        public static string Compute(byte[] xmlBytes)
        {
            return ComputeInternal(xmlBytes, false);
        }

        public static string ComputeRuntime(byte[] xmlBytes)
        {
            // A running co-op world may have crossed one or more recovery
            // loader boundaries. Keep the checksum tolerant of the exact same
            // loader-only mutations used by restore verification, while still
            // hashing durable progress, identity, counts and positions.
            return ComputeInternal(xmlBytes, true);
        }

        public static string ComputeForLoadedWorld(byte[] xmlBytes)
        {
            return ComputeRuntime(xmlBytes);
        }

        public static CoopXmlDifference FindFirstLoadedWorldDifference(
            byte[] authoritativeXml,
            byte[] loadedXml)
        {
            XmlDocument authoritative = NormalizeDocument(authoritativeXml, true);
            XmlDocument loaded = NormalizeDocument(loadedXml, true);
            return CoopDiagnostics.FindFirstDifference(
                Encoding.UTF8.GetBytes(authoritative.OuterXml),
                Encoding.UTF8.GetBytes(loaded.OuterXml));
        }

        private static string ComputeInternal(byte[] xmlBytes, bool normalizeLoaderMutations)
        {
            XmlDocument document = NormalizeDocument(xmlBytes, normalizeLoaderMutations);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(document.OuterXml));
                StringBuilder result = new StringBuilder(16);
                for (int i = 0; i < 8; i++) result.Append(hash[i].ToString("x2"));
                return result.ToString();
            }
        }

        private static XmlDocument NormalizeDocument(
            byte[] xmlBytes,
            bool normalizeLoaderMutations)
        {
            if (xmlBytes == null) throw new ArgumentNullException("xmlBytes");

            XmlDocument document = CoopDiagnostics.LoadXmlDocument(xmlBytes);
            XmlElement root = document.DocumentElement;
            if (root != null)
            {
                foreach (string name in LocalOnlyNodes)
                {
                    XmlNode node = root[name];
                    if (node != null) root.RemoveChild(node);
                }
                foreach (string name in TolerantTimerNodes)
                    NormalizeNumericAttributes(root[name], TimerDecimals, false);
                RemovePresentationNames(root);
                NormalizeSpatialAttributes(root, SpatialDecimals);
                NormalizeContinuousNeedAttributes(root);
                NormalizeGroundedResourcePositions(root);
                RemoveCharacterOrientations(root);
                NormalizeContinuousStorageValues(root);
                RemoveResourceOrientations(root);
                NormalizeLoadedResourceTransforms(root);
                NormalizeEntityOrder(root);
                if (normalizeLoaderMutations) NormalizeLoaderMutations(root);
            }
            return document;
        }

        private static void NormalizeSpatialAttributes(XmlNode node, int decimals)
        {
            if (node == null) return;
            string name = node.Name ?? string.Empty;
            if (name.IndexOf("position", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("orientation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("rotation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                NormalizeNumericAttributes(node, decimals, true);
            }
            foreach (XmlNode child in node.ChildNodes)
                NormalizeSpatialAttributes(child, decimals);
        }

        private static void RemovePresentationNames(XmlNode root)
        {
            if (root == null) return;
            for (int i = 0; i < PresentationNameParents.Length; i++)
            {
                XmlNode parent = root[PresentationNameParents[i]];
                if (parent != null) RemoveNameNodes(parent);
            }
        }

        private static void NormalizeEntityOrder(XmlNode root)
        {
            if (root == null) return;
            foreach (string sectionName in OrderInsensitiveEntitySections)
            {
                XmlNode section = root[sectionName];
                if (section == null) continue;

                var entities = new List<XmlElement>();
                foreach (XmlNode child in section.ChildNodes)
                {
                    XmlElement element = child as XmlElement;
                    if (element == null || element["id"] == null) continue;
                    entities.Add(element);
                }
                if (entities.Count < 2) continue;

                entities.Sort(delegate(XmlElement left, XmlElement right)
                {
                    int leftId = ReadEntityId(left);
                    int rightId = ReadEntityId(right);
                    int idCompare = leftId.CompareTo(rightId);
                    if (idCompare != 0) return idCompare;
                    return StringComparer.Ordinal.Compare(left.Name, right.Name);
                });

                foreach (XmlElement entity in entities) section.RemoveChild(entity);
                foreach (XmlElement entity in entities) section.AppendChild(entity);
            }
        }

        private static int ReadEntityId(XmlElement entity)
        {
            int value;
            return int.TryParse(entity["id"]?.GetAttribute("value"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : int.MaxValue;
        }

        private static HashSet<string> FindAnchoredCharacterIds(XmlElement root)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            XmlNode interactions = root["interactions"];
            if (interactions == null) return ids;
            foreach (XmlNode child in interactions.ChildNodes)
            {
                XmlElement interaction = child as XmlElement;
                if (interaction == null || interaction["interaction-point"] == null) continue;
                string id = interaction["character"]?.GetAttribute("id");
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }

        private static void NormalizeLoaderMutations(XmlElement root)
        {
            XmlNode constructions = root["constructions"];
            if (constructions != null)
            {
                foreach (XmlNode child in constructions.ChildNodes)
                {
                    XmlElement construction = child as XmlElement;
                    if (construction == null ||
                        !StringComparer.Ordinal.Equals(construction.Name, "construction")) continue;
                    NormalizeToLoaderFloatPrecision(construction["time-built"]);
                    NormalizeLoaderIncompleteConstructionState(construction);
                }
            }

            XmlNode characters = root["characters"];
            if (characters != null)
            {
                HashSet<string> anchored = FindAnchoredCharacterIds(root);
                foreach (XmlNode child in characters.ChildNodes)
                {
                    XmlElement character = child as XmlElement;
                    if (character == null) continue;
                    XmlNode state = character["state"];
                    if (state != null) character.RemoveChild(state);
                    // Yukleyici noktaya bagli etkilesimdeki karakteri o noktaya oturtur
                    // (canli: 1117.1 -> 1117.4, iki peer'de ayni). Kimlik ve etkilesim hash'te kalir.
                    XmlNode position = character["position"];
                    if (position != null &&
                        anchored.Contains(character["id"]?.GetAttribute("value") ?? string.Empty))
                        character.RemoveChild(position);
                }
            }

            NormalizeStoredResourceTransforms(root);

            XmlNode resources = root["resources"];
            if (resources == null) return;
            foreach (XmlNode child in resources.ChildNodes)
            {
                XmlElement resource = child as XmlElement;
                if (resource == null || !StringComparer.Ordinal.Equals(resource.Name, "resource")) continue;
                string state = resource["state"]?.GetAttribute("value");
                if (StringComparer.Ordinal.Equals(state, "1") ||
                    (StringComparer.Ordinal.Equals(state, "0") &&
                     StringComparer.Ordinal.Equals(
                         resource["location"]?.GetAttribute("value"), "1")))
                    RemoveLoadedResourceTransform(resource);
            }
        }

        private static void NormalizeStoredResourceTransforms(XmlElement root)
        {
            // Vanilla repositions a resource to the storage-slot anchor while
            // rebuilding a save. The resource identity, type, state and
            // location remain authoritative; this nested transform is purely
            // a loader/layout artifact (observed y=0.2 -> y=0.4 in production).
            XmlNodeList storedResources = root == null
                ? null
                : root.SelectNodes(".//resource-storage/slot/resource");
            if (storedResources == null) return;
            foreach (XmlNode node in storedResources)
                RemoveLoadedResourceTransform(node as XmlElement);
        }

        private static void NormalizeLoaderIncompleteConstructionState(
            XmlElement construction)
        {
            if (construction == null ||
                construction["build-progress"] == null ||
                construction["construction-materials"] == null) return;

            string type = construction.GetAttribute("type");
            if (!StringComparer.Ordinal.Equals(type, "Module") &&
                !StringComparer.Ordinal.Equals(type, "Connection")) return;

            double timeBuilt;
            if (!double.TryParse(
                    construction["time-built"]?.GetAttribute("value"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out timeBuilt) || Math.Abs(timeBuilt) > 0.000001d) return;

            XmlElement state = construction["state"];
            string value = state?.GetAttribute("value");
            if (StringComparer.Ordinal.Equals(value, "1") ||
                StringComparer.Ordinal.Equals(value, "2"))
                state.SetAttribute("value", "1");
        }

        private static void NormalizeGroundedResourcePositions(XmlElement root)
        {
            XmlNode resources = root == null ? null : root["resources"];
            if (resources == null) return;
            foreach (XmlNode child in resources.ChildNodes)
            {
                XmlElement resource = child as XmlElement;
                if (resource == null ||
                    !StringComparer.Ordinal.Equals(resource.Name, "resource") ||
                    !StringComparer.Ordinal.Equals(
                        resource["state"]?.GetAttribute("value"), "0") ||
                    !StringComparer.Ordinal.Equals(
                        resource["location"]?.GetAttribute("value"), "1")) continue;
                XmlElement position = resource["position"];
                if (position == null) continue;
                foreach (XmlAttribute attribute in position.Attributes)
                {
                    double value;
                    if (!double.TryParse(attribute.Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value)) continue;
                    const double bucketSize = 0.5d;
                    double normalized = Math.Round(
                        value / bucketSize, 0, MidpointRounding.AwayFromZero) * bucketSize;
                    attribute.Value = normalized.ToString(
                        "0.###", CultureInfo.InvariantCulture);
                }
            }
        }

        private static void RemoveCharacterOrientations(XmlElement root)
        {
            XmlNode characters = root == null ? null : root["characters"];
            if (characters == null) return;
            foreach (XmlNode child in characters.ChildNodes)
            {
                XmlElement character = child as XmlElement;
                if (character == null) continue;
                XmlNode orientation = character["orientation"];
                if (orientation != null) character.RemoveChild(orientation);
            }
        }

        private static void NormalizeContinuousStorageValues(XmlElement root)
        {
            XmlNode constructions = root == null ? null : root["constructions"];
            if (constructions == null) return;
            foreach (XmlNode child in constructions.ChildNodes)
            {
                XmlElement construction = child as XmlElement;
                if (construction == null) continue;
                NormalizeStorageValue(construction["power-storage"]);
                NormalizeStorageValue(construction["water-storage"]);
            }
        }

        private static void NormalizeStorageValue(XmlElement storage)
        {
            XmlAttribute valueAttribute = storage?.Attributes?["value"];
            decimal value;
            if (valueAttribute == null || !decimal.TryParse(
                    valueAttribute.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value)) return;

            const decimal bucketSize = 100000m;
            valueAttribute.Value = decimal.Truncate(value / bucketSize).ToString(
                "0",
                CultureInfo.InvariantCulture);
        }

        private static void NormalizeLoadedResourceTransforms(XmlElement root)
        {
            XmlNode resources = root["resources"];
            if (resources == null) return;
            foreach (XmlNode child in resources.ChildNodes)
            {
                XmlElement resource = child as XmlElement;
                if (resource == null ||
                    !StringComparer.Ordinal.Equals(resource.Name, "resource") ||
                    !IsTransientResourceState(resource["state"]?.GetAttribute("value"))) continue;
                RemoveLoadedResourceTransform(resource);
            }
        }

        private static void RemoveResourceOrientations(XmlElement root)
        {
            XmlNodeList resources = root == null ? null : root.SelectNodes(".//resource");
            if (resources == null) return;
            foreach (XmlNode child in resources)
            {
                XmlElement resource = child as XmlElement;
                if (resource == null) continue;
                XmlNode orientation = resource["orientation"];
                if (orientation != null) resource.RemoveChild(orientation);
            }
        }

        private static bool IsTransientResourceState(string state)
        {
            int value;
            return int.TryParse(state, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
        }

        private static void RemoveLoadedResourceTransform(XmlElement resource)
        {
            if (resource == null) return;
            XmlNode position = resource["position"];
            XmlNode orientation = resource["orientation"];
            if (position != null) resource.RemoveChild(position);
            if (orientation != null) resource.RemoveChild(orientation);
        }

        private static void RemoveNameNodes(XmlNode node)
        {
            if (node == null) return;
            for (int i = node.ChildNodes.Count - 1; i >= 0; i--)
            {
                XmlNode child = node.ChildNodes[i];
                if (child.NodeType == XmlNodeType.Element &&
                    StringComparer.OrdinalIgnoreCase.Equals(child.Name, "name"))
                {
                    node.RemoveChild(child);
                    continue;
                }
                RemoveNameNodes(child);
            }
        }

        private static void NormalizeContinuousNeedAttributes(XmlNode node)
        {
            if (node == null) return;
            foreach (string name in ContinuousNeedNodes)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(node.Name, name))
                {
                    NormalizeNumericAttributes(node, ContinuousNeedDecimals, false);
                    return;
                }
            }

            foreach (XmlNode child in node.ChildNodes)
                NormalizeContinuousNeedAttributes(child);
        }

        private static void NormalizeNumericAttributes(XmlNode node, int decimals, bool includeAllAttributes)
        {
            if (node == null) return;
            if (node.Attributes != null)
            {
                foreach (XmlAttribute attribute in node.Attributes)
                {
                    if (!includeAllAttributes && !StringComparer.Ordinal.Equals(attribute.Name, "value")) continue;
                    if (attribute.Value.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) continue;
                    double value;
                    if (!double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) continue;
                    double normalized = value >= 0.0
                        ? Math.Floor(value * Math.Pow(10.0, decimals)) / Math.Pow(10.0, decimals)
                        : Math.Ceiling(value * Math.Pow(10.0, decimals)) / Math.Pow(10.0, decimals);
                    attribute.Value = normalized.ToString("0.################", CultureInfo.InvariantCulture);
                }
            }
            if (!includeAllAttributes)
            {
                foreach (XmlNode child in node.ChildNodes)
                    NormalizeNumericAttributes(child, decimals, false);
            }
        }

        /// <summary>
        /// Vanilla Construction time-built'i double yazar ama float okur. Ayni
        /// float yuvarlamasini uygulamak yukleyici kaybini birebir yansitir;
        /// ondalik kovalari (3/4 hane) ise sinira denk gelen degerlerde
        /// iki tarafi farkli yone yuvarlayip sahte recovery hatasi uretiyordu.
        /// </summary>
        private static void NormalizeToLoaderFloatPrecision(XmlNode node)
        {
            XmlAttribute attribute = node == null || node.Attributes == null
                ? null
                : node.Attributes["value"];
            if (attribute == null) return;

            float value;
            if (!float.TryParse(attribute.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value)) return;
            attribute.Value = value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    public sealed class CoopStartupCoordinator
    {
        private readonly CoopRole mRole;
        private readonly long mSessionId;
        private readonly string mWorldHash;
        private readonly long mWorldTick;
        private DateTime mLastProgressUtc;
        private bool mLocalWorldReady;
        private bool mPeerReady;

        public CoopStartupCoordinator(CoopRole role, long sessionId, string worldHash, long worldTick)
        {
            mRole = role;
            mSessionId = sessionId;
            mWorldHash = worldHash ?? string.Empty;
            mWorldTick = worldTick;
            mLastProgressUtc = DateTime.UtcNow;
            Phase = CoopStartupPhase.WaitingForLocalWorld;
        }

        public CoopStartupPhase Phase { get; private set; }
        public bool CanStart => Phase == CoopStartupPhase.ReadyToStart || Phase == CoopStartupPhase.Running;
        public bool LocalWorldReady => mLocalWorldReady;
        public bool PeerReady => mPeerReady;
        public long SessionId => mSessionId;
        public long WorldTick => mWorldTick;
        public string WorldHash => mWorldHash;

        public void MarkLocalWorldReady()
        {
            if (Phase == CoopStartupPhase.Failed || Phase == CoopStartupPhase.Running)
                return;
            mLocalWorldReady = true;
            Phase = mPeerReady ? CoopStartupPhase.ReadyToStart : CoopStartupPhase.WaitingForPeer;
            Touch();
        }

        public bool AcceptPeerReady(long sessionId, string worldHash, long tick)
        {
            if (mRole != CoopRole.Host || Phase == CoopStartupPhase.Failed || Phase == CoopStartupPhase.Running)
                return false;
            if (sessionId != mSessionId || tick != mWorldTick || !StringComparer.Ordinal.Equals(worldHash, mWorldHash))
                return false;
            mPeerReady = true;
            Phase = mLocalWorldReady ? CoopStartupPhase.ReadyToStart : CoopStartupPhase.WaitingForLocalWorld;
            Touch();
            return true;
        }

        public bool AcceptStart(long sessionId, long tick)
        {
            if (mRole != CoopRole.Guest || !mLocalWorldReady || Phase == CoopStartupPhase.Failed)
                return false;
            if (sessionId != mSessionId || tick != mWorldTick)
                return false;
            Phase = CoopStartupPhase.ReadyToStart;
            Touch();
            return true;
        }

        public bool AuthorizeStart()
        {
            if (mRole != CoopRole.Host || Phase != CoopStartupPhase.ReadyToStart)
                return false;
            Phase = CoopStartupPhase.Running;
            Touch();
            return true;
        }

        public bool MarkStarted()
        {
            if (Phase != CoopStartupPhase.ReadyToStart)
                return false;
            Phase = CoopStartupPhase.Running;
            Touch();
            return true;
        }

        public bool HasTimedOut(DateTime nowUtc, TimeSpan timeout)
        {
            return Phase != CoopStartupPhase.Running && Phase != CoopStartupPhase.Failed &&
                nowUtc - mLastProgressUtc >= timeout;
        }

        public void Fail()
        {
            Phase = CoopStartupPhase.Failed;
            Touch();
        }

        private void Touch()
        {
            mLastProgressUtc = DateTime.UtcNow;
        }
    }

    public enum CoopHashMatch
    {
        Pending,
        Match,
        Mismatch
    }

    public sealed class CoopTickHashMatcher
    {
        private const int MaxPendingEntriesPerSide = 32;
        private readonly Dictionary<long, string> mLocal = new Dictionary<long, string>();
        private readonly Dictionary<long, string> mPeer = new Dictionary<long, string>();

        public int PendingEntryCount
        {
            get { return mLocal.Count + mPeer.Count; }
        }

        public void Clear()
        {
            mLocal.Clear();
            mPeer.Clear();
        }

        public CoopHashMatch RecordLocal(long tick, string hash)
        {
            mLocal[tick] = hash ?? string.Empty;
            Trim(mLocal);
            return Evaluate(tick);
        }

        public CoopHashMatch RecordPeer(long tick, string hash)
        {
            mPeer[tick] = hash ?? string.Empty;
            Trim(mPeer);
            return Evaluate(tick);
        }

        private CoopHashMatch Evaluate(long tick)
        {
            string local;
            string peer;
            if (!mLocal.TryGetValue(tick, out local) || !mPeer.TryGetValue(tick, out peer))
                return CoopHashMatch.Pending;
            mLocal.Remove(tick);
            mPeer.Remove(tick);
            return StringComparer.Ordinal.Equals(local, peer) ? CoopHashMatch.Match : CoopHashMatch.Mismatch;
        }

        private static void Trim(Dictionary<long, string> entries)
        {
            while (entries.Count > MaxPendingEntriesPerSide)
            {
                long oldest = long.MaxValue;
                foreach (long tick in entries.Keys)
                {
                    if (tick < oldest) oldest = tick;
                }
                entries.Remove(oldest);
            }
        }
    }
}
