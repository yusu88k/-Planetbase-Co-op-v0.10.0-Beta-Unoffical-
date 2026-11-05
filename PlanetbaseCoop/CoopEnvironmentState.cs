using System;
using System.Globalization;
using System.IO;
using System.Xml;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Deterministik ruzgar fazi. Vanilla EnvironmentManager.serialize yalnizca
    /// time-of-day ve wind-indicator degerlerini yazar; mTimeToWindChange,
    /// mWindOffset ve mWindDirection serialize edilmez. Bu alanlar restore
    /// edilmedigi surece host ve guest farkli ticklerde Random.Range cagirir,
    /// turbin uretimi ayrisir ve power-storage sahte OOS uretir.
    /// </summary>
    public struct CoopEnvironmentWindState
    {
        public float TimeToWindChange;
        public float WindOffset;
        public float WindDirectionX;
        public float WindDirectionY;
        public float WindDirectionZ;

        public bool IsValid()
        {
            return IsFinite(TimeToWindChange) && IsFinite(WindOffset) &&
                IsFinite(WindDirectionX) && IsFinite(WindDirectionY) &&
                IsFinite(WindDirectionZ);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public static class CoopEnvironmentState
    {
        public const string ElementName = "coop-environment";
        private const string ChildTimeToWindChange = "time-to-wind-change";
        private const string ChildWindOffset = "wind-offset";
        private const string ChildWindX = "wind-x";
        private const string ChildWindY = "wind-y";
        private const string ChildWindZ = "wind-z";

        public static bool TryRead(XmlElement saveRoot, out CoopEnvironmentWindState state)
        {
            state = default(CoopEnvironmentWindState);
            if (saveRoot == null) return false;
            XmlNodeList matches = saveRoot.SelectNodes(ElementName);
            if (matches == null || matches.Count != 1) return false;
            XmlElement node = matches[0] as XmlElement;
            if (node == null) return false;
            return TryReadChild(node, ChildTimeToWindChange, out state.TimeToWindChange) &&
                TryReadChild(node, ChildWindOffset, out state.WindOffset) &&
                TryReadChild(node, ChildWindX, out state.WindDirectionX) &&
                TryReadChild(node, ChildWindY, out state.WindDirectionY) &&
                TryReadChild(node, ChildWindZ, out state.WindDirectionZ) &&
                state.IsValid();
        }

        public static void Write(XmlNode saveRoot, CoopEnvironmentWindState state)
        {
            if (saveRoot == null) throw new ArgumentNullException("saveRoot");
            XmlElement rootElement = saveRoot as XmlElement;
            if (rootElement == null) throw new InvalidDataException("environment state requires an element root");
            if (!state.IsValid())
                throw new ArgumentOutOfRangeException("state", "environment wind state is invalid");

            XmlNodeList existing = rootElement.SelectNodes(ElementName);
            if (existing != null)
            {
                for (int i = existing.Count - 1; i >= 0; i--)
                    rootElement.RemoveChild(existing[i]);
            }

            XmlDocument owner = rootElement.OwnerDocument ?? saveRoot as XmlDocument;
            if (owner == null) throw new InvalidDataException("save-game has no XML document");
            XmlElement element = owner.CreateElement(ElementName);
            AppendValue(element, owner, ChildTimeToWindChange, Format(state.TimeToWindChange));
            AppendValue(element, owner, ChildWindOffset, Format(state.WindOffset));
            AppendValue(element, owner, ChildWindX, Format(state.WindDirectionX));
            AppendValue(element, owner, ChildWindY, Format(state.WindDirectionY));
            AppendValue(element, owner, ChildWindZ, Format(state.WindDirectionZ));
            rootElement.AppendChild(element);
        }

        private static void AppendValue(XmlElement parent, XmlDocument owner, string name, string value)
        {
            XmlElement child = owner.CreateElement(name);
            child.SetAttribute("value", value);
            parent.AppendChild(child);
        }

        private static bool TryReadChild(XmlElement node, string name, out float value)
        {
            value = 0f;
            XmlNodeList matches = node.SelectNodes(name);
            if (matches == null || matches.Count != 1) return false;
            XmlAttribute valueAttribute = matches[0].Attributes?["value"];
            string text = valueAttribute == null ? null : valueAttribute.Value;
            return !string.IsNullOrEmpty(text) &&
                float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
