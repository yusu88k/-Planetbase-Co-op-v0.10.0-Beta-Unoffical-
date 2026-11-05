using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace PlanetbaseCoop
{
    public sealed class CoopSnapshotValidationResult
    {
        internal CoopSnapshotValidationResult(
            bool isValid,
            string code,
            string message,
            string path,
            int entityId)
        {
            IsValid = isValid;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Path = path ?? string.Empty;
            EntityId = entityId;
        }

        public bool IsValid { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public string Path { get; private set; }
        public int EntityId { get; private set; }
    }

    /// <summary>
    /// Admission boundary for snapshots that can be written or loaded. This is
    /// intentionally stricter than the MerchantShip-specific invariant.
    /// </summary>
    public static class CoopSnapshotInvariant
    {
        public const int WorldPacketHeaderBytes = 1 + 8 + 8 + 4;
        public const int MaxSnapshotBytes = CoopDiagnostics.MaxXmlBytes;
        public const int SupportedSaveVersion = 12;

        private static readonly string[] RequiredTopLevelNodes =
        {
            "id-generator",
            "planet",
            "terrain",
            "constructions",
            "characters",
            "resources",
            "ships",
            "interactions",
            "ship-manager"
        };

        public static CoopSnapshotValidationResult Validate(byte[] xmlBytes)
        {
            if (xmlBytes == null || xmlBytes.Length == 0)
                return Invalid("empty-snapshot", "Snapshot XML is empty.", string.Empty, -1);
            if (xmlBytes.Length > MaxSnapshotBytes)
                return Invalid("snapshot-too-large", "Snapshot exceeds the world packet limit.", string.Empty, -1);

            XmlDocument document;
            try
            {
                document = CoopDiagnostics.LoadXmlDocument(xmlBytes);
            }
            catch (XmlException ex)
            {
                return Invalid("malformed-xml", "Snapshot XML is malformed or exceeds safety limits: " + ex.Message,
                    string.Empty, -1);
            }

            XmlElement root = document.DocumentElement;
            if (root == null || !StringComparer.Ordinal.Equals(root.Name, "save-game"))
                return Invalid("invalid-root", "Snapshot root must be 'save-game'.",
                    root == null ? string.Empty : CoopDiagnostics.GetPath(root), -1);

            int version;
            if (!int.TryParse(root.GetAttribute("version"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out version) || version != SupportedSaveVersion)
                return Invalid("invalid-version", "Snapshot save-game version is not supported.",
                    CoopDiagnostics.GetPath(root) + "/@version", -1);

            CoopSnapshotValidationResult attributeShapes = ValidateAttributeShapes(document);
            if (!attributeShapes.IsValid) return attributeShapes;

            for (int i = 0; i < RequiredTopLevelNodes.Length; i++)
            {
                string nodeName = RequiredTopLevelNodes[i];
                XmlNodeList matches = root.SelectNodes(nodeName);
                if (matches == null || matches.Count != 1)
                    return Invalid("invalid-section", "Snapshot must contain exactly one top-level '" +
                        nodeName + "' section.", CoopDiagnostics.GetPath(root) + "/" + nodeName + "[1]", -1);
            }

            CoopSnapshotValidationResult structure = ValidateLoaderStructure(root);
            if (!structure.IsValid) return structure;

            MerchantShipSnapshotValidationResult merchant = MerchantShipSnapshotInvariant.Validate(document);
            if (!merchant.IsValid)
                return Invalid(merchant.Code, merchant.Message, merchant.Path, merchant.MerchantShipId);

            return new CoopSnapshotValidationResult(
                true, "valid", "Snapshot invariants are valid.", string.Empty, -1);
        }

        private static CoopSnapshotValidationResult ValidateAttributeShapes(XmlDocument document)
        {
            string[] selectableTypes =
            {
                "Bot", "Colonist", "Guest", "Module", "Connection", "ConstructionComponent",
                "Resource", "ColonyShip", "ColonistShip", "MerchantShip", "VisitorShip"
            };
            XmlNodeList elements = document.SelectNodes("//*");
            foreach (XmlNode element in elements)
            {
                XmlAttributeCollection attributes = element.Attributes;
                int count = attributes == null ? 0 : attributes.Count;
                if (count == 0) continue;

                if (count == 1)
                {
                    string name = attributes[0].Name;
                    if (StringComparer.Ordinal.Equals(name, "value")) continue;
                    if (StringComparer.Ordinal.Equals(name, "type") &&
                        IsSafeSerializedTypeName(attributes[0].Value)) continue;
                    if (StringComparer.Ordinal.Equals(name, "version") &&
                        ReferenceEquals(element, document.DocumentElement)) continue;
                    return Invalid("invalid-attribute-shape",
                        "Snapshot element has an unsupported attribute layout.",
                        CoopDiagnostics.GetPath(element), -1);
                }

                if (count == 2 &&
                    StringComparer.Ordinal.Equals(attributes[0].Name, "id") &&
                    StringComparer.Ordinal.Equals(attributes[1].Name, "type-name"))
                {
                    int id;
                    if (int.TryParse(attributes[0].Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out id) && id >= 0 &&
                        ContainsOrdinal(selectableTypes, attributes[1].Value)) continue;
                    return Invalid("invalid-reference", "Snapshot contains an invalid selectable reference.",
                        CoopDiagnostics.GetPath(element), -1);
                }

                if (count == 3 &&
                    StringComparer.Ordinal.Equals(attributes[0].Name, "x") &&
                    StringComparer.Ordinal.Equals(attributes[1].Name, "y") &&
                    StringComparer.Ordinal.Equals(attributes[2].Name, "z") &&
                    IsFiniteFloat(attributes[0].Value) && IsFiniteFloat(attributes[1].Value) &&
                    IsFiniteFloat(attributes[2].Value)) continue;

                return Invalid("invalid-attribute-shape",
                    "Snapshot element has an unsupported or out-of-order attribute layout.",
                    CoopDiagnostics.GetPath(element), -1);
            }
            return Valid();
        }

        private static CoopSnapshotValidationResult ValidateLoaderStructure(XmlElement root)
        {
            CoopSnapshotValidationResult result;
            if (!RequireIntValue(root["id-generator"], "next-id", 0, int.MaxValue, out result)) return result;
            if (!RequireIntValue(root["id-generator"], "next-bot-id", 0, int.MaxValue, out result)) return result;
            if (!RequireIntValue(root["planet"], "planet-index", 0, 3, out result)) return result;
            if (!RequireIntValue(root["terrain"], "seed", int.MinValue, int.MaxValue, out result)) return result;
            result = ValidateShipManager(root);
            if (!result.IsValid) return result;

            XmlNode resources = root["resources"];
            XmlNodeList inmaterial = resources == null ? null : resources.SelectNodes("inmaterial-resources");
            if (inmaterial == null || inmaterial.Count != 1)
                return Invalid("invalid-section", "Resources must contain exactly one inmaterial-resources section.",
                    CoopDiagnostics.GetPath(resources) + "/inmaterial-resources[1]", -1);

            result = ValidateEntities(root["constructions"], "construction",
                new[] { "Module", "Connection" },
                new[] { "id", "position", "orientation", "state", "build-progress", "condition",
                    "oxygen", "enabled", "time-built", "locked", "high-priority" });
            if (!result.IsValid) return result;
            result = ValidateEntities(root["characters"], "character",
                new[] { "Bot", "Colonist", "Guest" },
                new[] { "id", "position", "orientation", "location", "name", "specialization",
                    "status-flags", "state", "wander-time" });
            if (!result.IsValid) return result;
            result = ValidateEntities(root["resources"], "resource",
                new[] { "AlcoholicDrink", "Bioplastic", "Coins", "Gun", "Meal", "MedicalSupplies",
                    "MedicinalPlants", "Metal", "Ore", "Semiconductors", "Spares", "Starch",
                    "Vegetables", "Vitromeat" },
                new[] { "id", "position", "orientation", "state", "location", "subtype", "durability" });
            if (!result.IsValid) return result;
            result = ValidateEntities(root["ships"], "ship",
                new[] { "ColonyShip", "ColonistShip", "MerchantShip", "VisitorShip" },
                new[] { "id", "position", "orientation", "state" });
            if (!result.IsValid) return result;
            result = ValidateIdentityGraph(root);
            if (!result.IsValid) return result;
            return ValidateInteractionEnvelope(root["interactions"]);
        }

        private static CoopSnapshotValidationResult ValidateIdentityGraph(XmlElement root)
        {
            var ids = new HashSet<int>();
            var moduleIds = new HashSet<int>();
            string[] sectionNames = { "constructions", "characters", "resources", "ships" };
            string[] entityNames = { "construction", "character", "resource", "ship" };

            for (int sectionIndex = 0; sectionIndex < sectionNames.Length; sectionIndex++)
            {
                XmlNode section = root[sectionNames[sectionIndex]];
                foreach (XmlNode entity in section.ChildNodes)
                {
                    if (entity.NodeType != XmlNodeType.Element ||
                        !StringComparer.Ordinal.Equals(entity.Name, entityNames[sectionIndex]))
                        continue;
                    CoopSnapshotValidationResult registration =
                        RegisterEntityId(entity, ids);
                    if (!registration.IsValid) return registration;
                    if (sectionIndex == 0 &&
                        StringComparer.Ordinal.Equals(entity.Attributes?["type"]?.Value, "Module"))
                    {
                        moduleIds.Add(ReadEntityId(entity));
                    }
                }
            }

            XmlNode constructions = root["constructions"];
            foreach (XmlNode construction in constructions.ChildNodes)
            {
                if (construction.NodeType != XmlNodeType.Element ||
                    !StringComparer.Ordinal.Equals(construction.Name, "construction")) continue;
                string constructionType = construction.Attributes?["type"]?.Value;
                if (StringComparer.Ordinal.Equals(constructionType, "Module"))
                {
                    XmlNode components = construction["components"];
                    if (components == null) continue;
                    foreach (XmlNode component in components.ChildNodes)
                    {
                        if (component.NodeType != XmlNodeType.Element) continue;
                        if (!StringComparer.Ordinal.Equals(component.Name, "component") ||
                            !StringComparer.Ordinal.Equals(
                                component.Attributes?["type"]?.Value,
                                "ConstructionComponent"))
                        {
                            return Invalid("invalid-entity-type",
                                "Module contains an unsupported component entity.",
                                CoopDiagnostics.GetPath(component), -1);
                        }
                        CoopSnapshotValidationResult registration =
                            RegisterEntityId(component, ids);
                        if (!registration.IsValid) return registration;
                    }
                    continue;
                }

                if (!StringComparer.Ordinal.Equals(constructionType, "Connection")) continue;
                XmlNode links = construction["links"];
                XmlNodeList linkIds = links == null ? null : links.SelectNodes("id");
                if (linkIds == null || linkIds.Count != 2)
                {
                    return Invalid("invalid-reference",
                        "Connection must reference exactly two modules.",
                        CoopDiagnostics.GetPath(construction) + "/links[1]",
                        ReadEntityId(construction));
                }

                int firstLink = -1;
                for (int i = 0; i < linkIds.Count; i++)
                {
                    XmlNode link = linkIds[i];
                    int linkId = -1;
                    if (link.Attributes == null || link.Attributes["value"] == null ||
                        !int.TryParse(link.Attributes["value"].Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out linkId) || linkId < 0 ||
                        !moduleIds.Contains(linkId) || (i == 1 && linkId == firstLink))
                    {
                        return Invalid("invalid-reference",
                            "Connection contains an invalid module reference.",
                            CoopDiagnostics.GetPath(link) + "/@value", linkId);
                    }
                    if (i == 0) firstLink = linkId;
                }
            }
            return Valid();
        }

        private static CoopSnapshotValidationResult RegisterEntityId(
            XmlNode entity,
            HashSet<int> ids)
        {
            XmlNode idNode = entity == null ? null : entity["id"];
            int entityId = -1;
            if (idNode == null || idNode.Attributes == null ||
                idNode.Attributes["value"] == null ||
                !int.TryParse(idNode.Attributes["value"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out entityId) || entityId < 0)
            {
                return Invalid("invalid-entity-id",
                    "Serialized selectable has an invalid entity id.",
                    CoopDiagnostics.GetPath(entity) + "/id[1]/@value", entityId);
            }
            if (!ids.Add(entityId))
            {
                return Invalid("duplicate-entity-id",
                    "Serialized selectable id is duplicated.",
                    CoopDiagnostics.GetPath(entity) + "/id[1]/@value", entityId);
            }
            return Valid();
        }

        private static CoopSnapshotValidationResult ValidateShipManager(XmlElement root)
        {
            XmlNode shipManager = root == null ? null : root["ship-manager"];
            CoopSnapshotValidationResult result;
            foreach (string name in new[]
            {
                "time-since-last-colonist-landing",
                "time-since-last-visitor-landing",
                "time-since-last-merchant-landing",
                "time-to-next-intruder"
            })
            {
                if (!RequireFiniteFloatValue(shipManager, name, out result)) return result;
            }

            float tickTime;
            if (!CoopShipManagerClock.TryRead(root, out tickTime))
                return Invalid("invalid-scalar", "Snapshot has no unique valid 'coop-tick-time' value.",
                    CoopDiagnostics.GetPath(shipManager) + "/coop-tick-time[1]/@value", -1);

            XmlNodeList permissionSections = shipManager == null
                ? null
                : shipManager.SelectNodes("landing-permissions");
            if (permissionSections == null || permissionSections.Count != 1)
                return Invalid("invalid-section",
                    "Ship manager must contain exactly one landing-permissions section.",
                    CoopDiagnostics.GetPath(shipManager) + "/landing-permissions[1]", -1);

            XmlNode permissions = permissionSections[0];
            foreach (string name in new[]
            {
                "colonists-allowed", "merchants-allowed", "visitors-allowed"
            })
            {
                if (!RequireBoolValue(permissions, name, out result)) return result;
            }
            return Valid();
        }

        private static CoopSnapshotValidationResult ValidateEntities(
            XmlNode section,
            string elementName,
            string[] allowedTypes,
            string[] requiredChildren)
        {
            if (section == null)
                return Invalid("invalid-section", "Snapshot section is missing.", string.Empty, -1);

            foreach (XmlNode entity in section.ChildNodes)
            {
                if (entity.NodeType != XmlNodeType.Element) continue;
                if (!StringComparer.Ordinal.Equals(entity.Name, elementName))
                {
                    if (StringComparer.Ordinal.Equals(section.Name, "resources") &&
                        StringComparer.Ordinal.Equals(entity.Name, "inmaterial-resources")) continue;
                    return Invalid("unexpected-entity", "Unexpected element in " + section.Name + ".",
                        CoopDiagnostics.GetPath(entity), -1);
                }

                string type = entity.Attributes == null ? null : entity.Attributes["type"]?.Value;
                if (!ContainsOrdinal(allowedTypes, type))
                    return Invalid("invalid-entity-type", "Unsupported " + elementName + " type '" +
                        (type ?? string.Empty) + "'.", CoopDiagnostics.GetPath(entity) + "/@type", -1);

                for (int i = 0; i < requiredChildren.Length; i++)
                {
                    if (entity[requiredChildren[i]] == null)
                        return Invalid("missing-entity-field", elementName + " type '" + type +
                            "' is missing required field '" + requiredChildren[i] + "'.",
                            CoopDiagnostics.GetPath(entity) + "/" + requiredChildren[i] + "[1]", ReadEntityId(entity));
                }

                XmlNode idNode = entity["id"];
                int entityId = -1;
                if (idNode == null || idNode.Attributes == null ||
                    idNode.Attributes["value"] == null ||
                    !int.TryParse(idNode.Attributes["value"].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out entityId) || entityId < 0)
                {
                    return Invalid("invalid-entity-id",
                        elementName + " type '" + type + "' has an invalid entity id.",
                        CoopDiagnostics.GetPath(entity) + "/id[1]/@value", entityId);
                }
            }
            return Valid();
        }

        private static CoopSnapshotValidationResult ValidateInteractionEnvelope(XmlNode section)
        {
            string[] allowedTypes =
            {
                "InteractionAirlockEnter", "InteractionAirlockExit", "InteractionBuild", "InteractionCombat",
                "InteractionComponentRestore", "InteractionConsumeResource", "InteractionConsumeResourceFloor",
                "InteractionOperate", "InteractionRepairBuildable", "InteractionRestore", "InteractionSleepFloor",
                "InteractionWaitForHealing", "InteractionWork"
            };
            foreach (XmlNode interaction in section.ChildNodes)
            {
                if (interaction.NodeType != XmlNodeType.Element) continue;
                if (!StringComparer.Ordinal.Equals(interaction.Name, "interaction"))
                    return Invalid("unexpected-entity", "Unexpected element in interactions.",
                        CoopDiagnostics.GetPath(interaction), -1);
                string type = interaction.Attributes == null ? null : interaction.Attributes["type"]?.Value;
                if (!ContainsOrdinal(allowedTypes, type))
                    return Invalid("invalid-entity-type", "Unsupported interaction type '" +
                        (type ?? string.Empty) + "'.", CoopDiagnostics.GetPath(interaction) + "/@type", -1);
                XmlNode character = interaction["character"];
                if (character == null || character.Attributes?["id"] == null ||
                    character.Attributes?["type-name"] == null)
                    return Invalid("missing-entity-field", "Interaction has no valid character reference.",
                        CoopDiagnostics.GetPath(interaction) + "/character[1]", -1);
            }
            return Valid();
        }

        private static bool RequireIntValue(
            XmlNode parent,
            string childName,
            int minimum,
            int maximum,
            out CoopSnapshotValidationResult result)
        {
            XmlNode child = parent == null ? null : parent[childName];
            int value;
            if (child == null || child.Attributes == null || child.Attributes["value"] == null ||
                !int.TryParse(child.Attributes["value"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
            {
                result = Invalid("invalid-scalar", "Snapshot has no valid '" + childName + "' value.",
                    CoopDiagnostics.GetPath(parent) + "/" + childName + "[1]/@value", -1);
                return false;
            }
            result = Valid();
            return true;
        }

        private static bool RequireFiniteFloatValue(
            XmlNode parent,
            string childName,
            out CoopSnapshotValidationResult result)
        {
            XmlNodeList matches = parent == null ? null : parent.SelectNodes(childName);
            string value = matches == null || matches.Count != 1 ||
                matches[0].Attributes == null || matches[0].Attributes["value"] == null
                ? null
                : matches[0].Attributes["value"].Value;
            if (!IsFiniteFloat(value))
            {
                result = Invalid("invalid-scalar", "Snapshot has no unique valid '" +
                    childName + "' value.", CoopDiagnostics.GetPath(parent) + "/" +
                    childName + "[1]/@value", -1);
                return false;
            }
            result = Valid();
            return true;
        }

        private static bool RequireBoolValue(
            XmlNode parent,
            string childName,
            out CoopSnapshotValidationResult result)
        {
            XmlNodeList matches = parent == null ? null : parent.SelectNodes(childName);
            bool value;
            string text = matches == null || matches.Count != 1 ||
                matches[0].Attributes == null || matches[0].Attributes["value"] == null
                ? null
                : matches[0].Attributes["value"].Value;
            if (!bool.TryParse(text, out value))
            {
                result = Invalid("invalid-scalar", "Snapshot has no unique valid '" +
                    childName + "' value.", CoopDiagnostics.GetPath(parent) + "/" +
                    childName + "[1]/@value", -1);
                return false;
            }
            result = Valid();
            return true;
        }

        private static bool ContainsOrdinal(string[] values, string candidate)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (StringComparer.Ordinal.Equals(values[i], candidate)) return true;
            }
            return false;
        }

        private static bool IsSafeSerializedTypeName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 96) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '_' || c == '-') continue;
                return false;
            }
            return true;
        }

        private static bool IsFiniteFloat(string value)
        {
            float parsed;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
                !float.IsNaN(parsed) && !float.IsInfinity(parsed);
        }

        private static int ReadEntityId(XmlNode entity)
        {
            int value;
            return int.TryParse(entity?["id"]?.Attributes?["value"]?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : -1;
        }

        private static CoopSnapshotValidationResult Valid()
        {
            return new CoopSnapshotValidationResult(true, "valid", "Snapshot invariants are valid.",
                string.Empty, -1);
        }

        private static CoopSnapshotValidationResult Invalid(
            string code,
            string message,
            string path,
            int entityId)
        {
            return new CoopSnapshotValidationResult(false, code, message, path, entityId);
        }
    }

    public static class CoopSnapshotAdmissionPolicy
    {
        public static bool IsDeferrableHostTradingState(bool isHost, string validationCode)
        {
            return isHost && StringComparer.Ordinal.Equals(
                validationCode, "uncommitted-trading-state");
        }

        public static bool IsDeferrableRuntimeTradingState(string validationCode)
        {
            return StringComparer.Ordinal.Equals(
                validationCode, "uncommitted-trading-state");
        }
    }
}
