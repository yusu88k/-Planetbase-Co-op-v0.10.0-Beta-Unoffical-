using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace PlanetbaseCoop
{
    public static class CoopShipManagerClock
    {
        public const string ElementName = "coop-tick-time";
        public const float MaximumTickTime = 1.1372f;

        public static bool TryRead(XmlNode saveRoot, out float value)
        {
            value = 0f;
            XmlNode shipManager = saveRoot == null ? null : saveRoot["ship-manager"];
            XmlNodeList matches = shipManager == null
                ? null
                : shipManager.SelectNodes(ElementName);
            if (matches == null || matches.Count != 1) return false;

            string text = matches[0].Attributes == null ||
                matches[0].Attributes["value"] == null
                ? null
                : matches[0].Attributes["value"].Value;
            return float.TryParse(text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value) &&
                !float.IsNaN(value) && !float.IsInfinity(value) &&
                value >= 0f && value <= MaximumTickTime;
        }

        public static bool TryReadLandingPermissions(
            XmlNode saveRoot,
            out CoopLandingPermissionsData permissions)
        {
            permissions = null;
            XmlNode node = saveRoot?["ship-manager"]?["landing-permissions"];
            bool colonists;
            bool merchants;
            bool visitors;
            if (node == null ||
                !TryReadBool(node["colonists-allowed"], out colonists) ||
                !TryReadBool(node["merchants-allowed"], out merchants) ||
                !TryReadBool(node["visitors-allowed"], out visitors)) return false;

            var percentages = new List<int>();
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element ||
                    !child.Name.EndsWith("-percentage", StringComparison.Ordinal)) continue;
                int value;
                if (!int.TryParse(child.Attributes?["value"]?.Value,
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
                percentages.Add(value);
            }

            var candidate = new CoopLandingPermissionsData
            {
                SessionId = 1,
                Sequence = 1,
                ApplyTick = 0,
                ColonistsAllowed = colonists,
                MerchantsAllowed = merchants,
                VisitorsAllowed = visitors,
                SpecializationPercentages = percentages.ToArray()
            };
            return CoopLandingPermissionsValidator.TryValidate(candidate, out permissions);
        }

        private static bool TryReadBool(XmlNode node, out bool value)
        {
            value = false;
            return bool.TryParse(node?.Attributes?["value"]?.Value, out value);
        }

        public static void Write(XmlNode saveRoot, float value)
        {
            if (saveRoot == null) throw new ArgumentNullException("saveRoot");
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < 0f || value > MaximumTickTime)
                throw new ArgumentOutOfRangeException("value");

            XmlNode shipManager = saveRoot["ship-manager"];
            if (shipManager == null)
                throw new InvalidDataException("save-game is missing ship-manager");

            XmlNodeList existing = shipManager.SelectNodes(ElementName);
            if (existing != null)
            {
                for (int i = existing.Count - 1; i >= 0; i--)
                    shipManager.RemoveChild(existing[i]);
            }

            XmlDocument owner = saveRoot.OwnerDocument ?? saveRoot as XmlDocument;
            if (owner == null) throw new InvalidDataException("save-game has no XML document");
            XmlElement element = owner.CreateElement(ElementName);
            element.SetAttribute("value", value.ToString("R", CultureInfo.InvariantCulture));
            shipManager.AppendChild(element);
        }
    }

    /// <summary>
    /// Verifies that the guest has actually applied the host-authoritative
    /// fields Planetbase mutates while loading. This check runs before READY;
    /// it never rewrites the compared snapshot or hides a real mismatch.
    /// </summary>
    public static class CoopLoadedWorldVerification
    {
        private static readonly string[] RestoredTopLevelNodes =
            { "id-generator", "sandstorm", "blizzard", "solar-flare", "ship-manager" };

        public static void AssertFrameIndexRestored(int expected, int actual)
        {
            if (expected < 0)
                throw new InvalidDataException("authoritative frame index is invalid");
            if (actual != expected)
            {
                throw new InvalidDataException(
                    "authoritative frame index was not restored: expected " + expected +
                    ", actual " + actual);
            }
        }

        public static void AssertRestoredState(byte[] loadedSnapshot, byte[] authoritativeSnapshot)
        {
            if (loadedSnapshot == null) throw new ArgumentNullException("loadedSnapshot");
            if (authoritativeSnapshot == null) throw new ArgumentNullException("authoritativeSnapshot");

            XmlDocument loaded = CoopDiagnostics.LoadXmlDocument(loadedSnapshot);
            XmlDocument authoritative = CoopDiagnostics.LoadXmlDocument(authoritativeSnapshot);
            XmlElement loadedRoot = RequireSaveRoot(loaded, "loaded snapshot");
            XmlElement authoritativeRoot = RequireSaveRoot(authoritative, "authoritative snapshot");

            ValidateIdGenerator(RequireUniqueChild(loadedRoot, "id-generator", "loaded snapshot"));
            ValidateIdGenerator(RequireUniqueChild(authoritativeRoot, "id-generator", "authoritative snapshot"));
            ValidateDisasters(loadedRoot, "loaded snapshot");
            ValidateDisasters(authoritativeRoot, "authoritative snapshot");
            ValidateShipManagerClock(loadedRoot, "loaded snapshot");
            ValidateShipManagerClock(authoritativeRoot, "authoritative snapshot");

            foreach (string name in RestoredTopLevelNodes)
            {
                XmlElement current = RequireUniqueChild(loadedRoot, name, "loaded snapshot");
                XmlElement source = RequireUniqueChild(authoritativeRoot, name, "authoritative snapshot");
                if (!StringComparer.Ordinal.Equals(current.OuterXml, source.OuterXml))
                {
                    throw new InvalidDataException(
                        "loaded snapshot did not restore authoritative /save-game/" + name);
                }
            }

            // Deterministik ruzgar fazi: DumpWorld bu node'u yalnizca
            // reflection alanlari cozulduyse yazar. Iki tarafta da yoksa
            // ozellik devre disi demektir; tek tarafda varsa tutarsizliktir.
            bool loadedHasEnvironment = loadedRoot.SelectNodes(
                CoopEnvironmentState.ElementName).Count == 1;
            bool authoritativeHasEnvironment = authoritativeRoot.SelectNodes(
                CoopEnvironmentState.ElementName).Count == 1;
            if (loadedHasEnvironment != authoritativeHasEnvironment)
            {
                throw new InvalidDataException(
                    "coop-environment present on only one side of the restore comparison");
            }
            if (loadedHasEnvironment)
            {
                XmlElement current = (XmlElement)loadedRoot.SelectNodes(
                    CoopEnvironmentState.ElementName)[0];
                XmlElement source = (XmlElement)authoritativeRoot.SelectNodes(
                    CoopEnvironmentState.ElementName)[0];
                if (!StringComparer.Ordinal.Equals(current.OuterXml, source.OuterXml))
                {
                    throw new InvalidDataException(
                        "loaded snapshot did not restore authoritative /save-game/coop-environment");
                }
            }
        }

        private static XmlElement RequireSaveRoot(XmlDocument document, string context)
        {
            XmlElement root = document == null ? null : document.DocumentElement;
            if (root == null || !StringComparer.Ordinal.Equals(root.Name, "save-game"))
                throw new InvalidDataException(context + " has no save-game root");
            return root;
        }

        private static XmlElement RequireUniqueChild(XmlElement root, string name, string context)
        {
            XmlElement match = null;
            foreach (XmlNode child in root.ChildNodes)
            {
                XmlElement element = child as XmlElement;
                if (element == null || !StringComparer.Ordinal.Equals(element.Name, name)) continue;
                if (match != null)
                    throw new InvalidDataException(context + " contains duplicate " + name);
                match = element;
            }
            if (match == null) throw new InvalidDataException(context + " is missing " + name);
            return match;
        }

        private static void ValidateIdGenerator(XmlElement node)
        {
            int nextId;
            int nextBotId;
            if (!TryReadNonNegativeInt(node["next-id"], out nextId) ||
                !TryReadNonNegativeInt(node["next-bot-id"], out nextBotId))
            {
                throw new InvalidDataException("invalid id-generator state");
            }
        }

        private static bool TryReadNonNegativeInt(XmlNode node, out int value)
        {
            value = -1;
            string text = node == null || node.Attributes == null || node.Attributes["value"] == null
                ? null
                : node.Attributes["value"].Value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        private static void ValidateDisasters(XmlElement root, string context)
        {
            foreach (string name in new[] { "sandstorm", "blizzard", "solar-flare" })
            {
                RequireUniqueChild(root, name, context);
                CoopDisasterState state;
                if (!CoopDisasterSnapshot.TryRead(root, name, out state) ||
                    float.IsNaN(state.TimeToNext) || float.IsInfinity(state.TimeToNext) ||
                    float.IsNaN(state.Time) || float.IsInfinity(state.Time) ||
                    float.IsNaN(state.Duration) || float.IsInfinity(state.Duration))
                {
                    throw new InvalidDataException(context + " contains invalid " + name + " state");
                }
            }
        }

        private static void ValidateShipManagerClock(XmlElement root, string context)
        {
            float value;
            if (!CoopShipManagerClock.TryRead(root, out value))
                throw new InvalidDataException(context + " contains invalid ship-manager clock");
        }
    }
}
