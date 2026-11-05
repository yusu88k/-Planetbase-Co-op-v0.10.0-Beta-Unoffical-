using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PlanetbaseCoop;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopLocalizationTests
    {
        private const string TurkishLetters = "ÇĞİÖŞÜçğıöşü";

        [Theory]
        [InlineData("BAŞLAT", "START")]
        [InlineData("MİSAFİR KATILDI • HAZIR DEĞİL", "GUEST JOINED • NOT READY")]
        [InlineData("HOST OYUNU DURAKLATTI", "HOST PAUSED THE GAME")]
        [InlineData("MISAFIR OYUNU 4X HIZA ALDI", "GUEST SET THE GAME TO 4X SPEED")]
        public void EnglishTranslatesExactUiText(string turkish, string english)
        {
            Assert.Equal(english, CoopText.Translate(turkish, CoopLanguage.English));
            Assert.Equal(turkish, CoopText.Translate(turkish, CoopLanguage.Turkish));
        }

        [Theory]
        [InlineData("Ayşe  •  ÇEVRİMİÇİ", "Ayşe  •  ONLINE")]
        [InlineData("DAVET GÖNDERİLDİ: Ayşe", "INVITE SENT: Ayşe")]
        [InlineData("DİKKAT: PİNG ÇOK YÜKSEK (412 MS) — OYUN YAVAŞLAYABİLİR",
            "WARNING: VERY HIGH PING (412 MS) — THE GAME MAY SLOW DOWN")]
        [InlineData("STEAM/PLANETBASE HAZIR DEĞİL", "STEAM/PLANETBASE NOT READY")]
        public void ComposedTextTranslatesFixedPartsAndKeepsNames(string turkish, string english)
        {
            Assert.Equal(english, CoopText.Translate(turkish, CoopLanguage.English));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("save10  •  10.10  14:00")]
        [InlineData("Keagan Heisse")]
        public void UnknownTextIsShownUnchanged(string text)
        {
            Assert.Equal(text, CoopText.Translate(text, CoopLanguage.English));
        }

        [Theory]
        [InlineData("tr", false, CoopLanguage.Turkish)]
        [InlineData(" EN ", true, CoopLanguage.English)]
        [InlineData("auto", true, CoopLanguage.Turkish)]
        [InlineData("auto", false, CoopLanguage.English)]
        [InlineData("garbage", false, CoopLanguage.English)]
        [InlineData(null, true, CoopLanguage.Turkish)]
        public void LanguageFollowsConfigThenWindowsLanguage(
            string configured, bool systemIsTurkish, CoopLanguage expected)
        {
            Assert.Equal(expected, CoopText.Resolve(configured, systemIsTurkish));
        }

        [Fact]
        public void EnglishTextContainsNoTurkishLetters()
        {
            foreach (KeyValuePair<string, string> pair in CoopText.AllTranslations())
            {
                Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key);
                Assert.True(pair.Value.IndexOfAny(TurkishLetters.ToCharArray()) < 0,
                    pair.Key + " -> " + pair.Value);
            }
        }

        // Yeni bir arayuz metni eklenip cevirisi unutulursa bu test yakalar.
        private static readonly string[] FullyScannedUiFiles =
            { "CoopLobbyUI.cs", "CoopStatusOverlay.cs" };
        private static readonly string[] SinkScannedFiles =
        {
            "SyncManager.cs", "CoopSteamService.cs", "PlacementSync.cs", "ShipSync.cs",
            "LandingPermissionsSync.cs", "CoopTradeSync.cs", "Plugin.cs"
        };
        private static readonly Regex Literal = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
        private static readonly Regex Sink = new Regex(
            "Notify\\(|return \"|sStatus = \"|sError = \"|CoopText\\.T\\(|RenderBigButton\\(|RenderPageTitle\\(");
        private static readonly HashSet<string> LanguageNeutral = new HashSet<string>(StringComparer.Ordinal)
        {
            "CO-OP", "HOST", "PORT:", "PORT  ", "STEAM/", "STEAM RELAY / ", "PLANETBASE",
            "SPACEWAR", "ERR", "PING  ", "GUEST", " PREVIEW: ", "DİL: TÜRKÇE", "LANGUAGE: ENGLISH"
        };

        [Fact]
        public void EveryUiStringHasAnEnglishTranslation()
        {
            string source = FindSourceDirectory();
            var missing = new List<string>();
            int checkedCount = 0;
            foreach (string file in FullyScannedUiFiles.Concat(SinkScannedFiles))
            {
                bool full = FullyScannedUiFiles.Contains(file);
                foreach (string line in File.ReadLines(Path.Combine(source, file)))
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || line.Contains("Log.Log") ||
                        line.Contains("LogInfo") || line.Contains("LogWarning") ||
                        line.Contains("LogError") || (!full && !Sink.IsMatch(line))) continue;
                    foreach (Match match in Literal.Matches(line))
                    {
                        string text = match.Groups[1].Value;
                        if (!IsUiText(text) || LanguageNeutral.Contains(text)) continue;
                        checkedCount++;
                        if (CoopText.Translate(text, CoopLanguage.English) == text)
                            missing.Add(file + ": " + text);
                    }
                }
            }
            Assert.True(checkedCount > 100, "scanner found only " + checkedCount + " UI strings");
            Assert.True(missing.Count == 0, "Missing translations:\n" + string.Join("\n", missing));
        }

        private static bool IsUiText(string text)
        {
            int letters = text.Count(char.IsLetter);
            return letters >= 3 && text == text.ToUpperInvariant() && !text.StartsWith("[");
        }

        private static string FindSourceDirectory()
        {
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
                 dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "PlanetbaseCoop");
                if (File.Exists(Path.Combine(candidate, "CoopLobbyUI.cs"))) return candidate;
            }
            throw new DirectoryNotFoundException("PlanetbaseCoop source directory not found");
        }
    }
}
