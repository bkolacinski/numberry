using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using TMPro;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Reflection;
using System.Collections.Generic;

namespace Numberry;

[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    private static Plugin _instance = null!;

    private static TMP_FontAsset _gameFont = null!;
    private static ConfigEntry<int> _fontSize = null!;
    private static ConfigEntry<float> _outlineWidth = null!;
    private static ConfigEntry<string> _textColorHex = null!;
    private static ConfigEntry<string> _outlineColorHex = null!;
    private static ConfigEntry<float> _textOffsetY = null!;
    private static ConfigEntry<float> _fontWidthScale = null!;
    private static ConfigEntry<float> _valueDivisor = null!;
    private static ConfigEntry<bool> _displayStaminaValue = null!;
    private static ConfigEntry<bool> _displayBonusStaminaValue = null!;
    private static ConfigEntry<bool> _displayBonusAsInteger = null!;
    private static ConfigEntry<float> _bonusTextOffsetY = null!;

    private static Color _textColorValue;
    private static Color _outlineColorValue;

    private static readonly Dictionary<StaminaBar, bool> InitializedBars = new();
    private static readonly Dictionary<string, TextMeshProUGUI> BarTexts = new();
    private static readonly Dictionary<string, float> LastKnownValues = new();
    private static readonly Dictionary<StaminaBar, float> MaxBonusWidths = new();

    private void Awake()
    {
        _instance = this;
        Log = Logger;

        // Load configuration
        _fontSize = Config.Bind("UI", "FontSize", 24, "Font size.");
        _outlineWidth = Config.Bind("UI", "OutlineWidth", 0.125f,
            "Outline width (0-1). Lower value gives a thinner outline.");
        _textColorHex = Config.Bind("UI", "TextColor", "#FFFFFF", "Text color.");
        _outlineColorHex = Config.Bind("UI", "OutlineColor", "#000000", "Outline color.");
        _textOffsetY = Config.Bind("UI", "TextOffsetY", 3.5f, "Text vertical offset (up/down).");
        _fontWidthScale = Config.Bind("UI", "FontWidthScale", 1.1f,
            "Font width scale (horizontal stretch, e.g. 1.1 for 110% width).");
        _valueDivisor = Config.Bind("UI", "ValueDivisor", 15f,
            "Divisor for converting UI width to stamina/bonus/affliction units (wiki uses 6, this mode uses 15).");
        _displayStaminaValue = Config.Bind("UI", "DisplayStamina", true, "Display raw stamina value.");
        _displayBonusStaminaValue = Config.Bind("UI", "DisplayBonusStaminaValue", true, "Display bonus stamina value.");
        _displayBonusAsInteger = Config.Bind("UI", "DisplayBonusAsInteger", true,
            "Display bonus stamina as integer (true) or with one decimal (false).");
        _bonusTextOffsetY = Config.Bind("UI", "BonusTextOffsetY", _textOffsetY.Value, "Bonus text vertical offset.");

        ColorUtility.TryParseHtmlString(_textColorHex.Value, out _textColorValue);
        ColorUtility.TryParseHtmlString(_outlineColorHex.Value, out _outlineColorValue);

        var h = new Harmony("com.peakmodding.peakmod");
        h.PatchAll(typeof(StaminaBar_Patches));
    }

    [HarmonyPatch]
    internal static class StaminaBar_Patches
    {
        [HarmonyPatch(typeof(StaminaBar), "Update")]
        [HarmonyPostfix]
        private static void UpdatePostfix(StaminaBar __instance)
        {
            try
            {
                if (!InitializedBars.ContainsKey(__instance) || !InitializedBars[__instance])
                {
                    InitializeBars(__instance);
                    InitializedBars[__instance] = true;
                }

                UpdateAllTexts(__instance);
            }
            catch (Exception e)
            {
                Log.LogError($"Error in StaminaBar.Update patch: {e}");
                if (__instance != null && !InitializedBars.ContainsKey(__instance))
                {
                    InitializedBars[__instance] = true;
                }
            }
        }
    }

    private static void InitializeBars(StaminaBar sb)
    {
        Log.LogInfo($"Initializing stamina info text objects for StaminaBar {sb.GetInstanceID()}.");

        foreach (var text in sb.GetComponentsInChildren<TextMeshProUGUI>())
        {
            if (text.name.EndsWith("ValueText"))
            {
                Destroy(text.gameObject);
            }
        }

        if (_gameFont == null)
        {
            _gameFont = GameObject.Find("GAME/GUIManager").GetComponent<GUIManager>().heroDayText.font;
        }

        if (_displayStaminaValue.Value)
        {
            var staminaBarField = sb.GetType().GetField("staminaBar",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (staminaBarField != null && staminaBarField.GetValue(sb) is Component staminaBarComp)
            {
                CreateTextObject(staminaBarComp.transform.parent.gameObject, $"Stamina_{sb.GetInstanceID()}",
                    _textOffsetY.Value);
            }
            else
            {
                Log.LogWarning("Could not find 'staminaBar' to attach stamina text.");
            }
        }

        if (_displayBonusStaminaValue.Value)
        {
            var extraBarField = sb.GetType().GetField("extraBarStamina",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (extraBarField != null && extraBarField.GetValue(sb) is Component extraBarComp)
            {
                CreateTextObject(extraBarComp.gameObject, $"BonusStamina_{sb.GetInstanceID()}",
                    _bonusTextOffsetY.Value);
            }
            else
            {
                Log.LogWarning("Could not find 'extraBarStamina' to attach bonus stamina text.");
            }
        }

        var afflictions = sb.GetComponentsInChildren<BarAffliction>(true);
        foreach (var affliction in afflictions)
        {
            CreateTextObject(affliction.gameObject, $"Affliction_{affliction.GetInstanceID()}", _textOffsetY.Value);
        }

        Log.LogInfo($"Finished initializing stamina info text objects for StaminaBar {sb.GetInstanceID()}.");
    }

    private static void CreateTextObject(GameObject parent, string key, float yOffset)
    {
        var textGo = new GameObject($"{key}_ValueText", typeof(RectTransform));
        textGo.transform.SetParent(parent.transform, false);

        var textMesh = textGo.AddComponent<TextMeshProUGUI>();
        textMesh.font = _gameFont;
        textMesh.alignment = TextAlignmentOptions.Center;
        textMesh.color = _textColorValue;
        textMesh.fontSize = _fontSize.Value;

        var mat = Instantiate(textMesh.fontSharedMaterial);
        mat.SetFloat(ShaderUtilities.ID_OutlineWidth, _outlineWidth.Value);
        mat.SetColor(ShaderUtilities.ID_OutlineColor, _outlineColorValue);
        mat.EnableKeyword("OUTLINE_ON");
        textMesh.fontMaterial = mat;
        textMesh.raycastTarget = false;

        var rect = textGo.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.anchoredPosition = new Vector2(0, yOffset);

        textGo.transform.localScale = new Vector3(_fontWidthScale.Value, 1f, 1f);

        BarTexts[key] = textMesh;
        LastKnownValues[key] = -1f;
        textMesh.gameObject.SetActive(false);
    }

    private static void UpdateAllTexts(StaminaBar sb)
    {
        string staminaKey = $"Stamina_{sb.GetInstanceID()}";
        if (_displayStaminaValue.Value && BarTexts.ContainsKey(staminaKey))
        {
            var staminaField = sb.GetType().GetField("desiredStaminaSize",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (staminaField != null)
            {
                float rawValue = (float)staminaField.GetValue(sb);
                UpdateText(staminaKey, rawValue, false);
            }
        }

        string bonusKey = $"BonusStamina_{sb.GetInstanceID()}";
        if (_displayBonusStaminaValue.Value && BarTexts.ContainsKey(bonusKey))
        {
            var bonusField = sb.GetType().GetField("desiredExtraStaminaSize",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var extraBarField = sb.GetType().GetField("extraBarStamina",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var staminaField = sb.GetType().GetField("desiredStaminaSize",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (bonusField != null && extraBarField != null && staminaField != null)
            {
                float bonusRawValue = (float)bonusField.GetValue(sb);
                float staminaRawValue = (float)staminaField.GetValue(sb);
                var extraBarComp = extraBarField.GetValue(sb) as Component;
                var extraBarRect = extraBarComp?.GetComponent<RectTransform>();

                if (extraBarRect != null && BarTexts.TryGetValue(bonusKey, out var textMesh))
                {
                    var textRect = textMesh.GetComponent<RectTransform>();
                    LastKnownValues.TryGetValue(bonusKey, out var lastBonusValue);
                    bool bonusStaminaDecreased = bonusRawValue < lastBonusValue;

                    if (staminaRawValue < 0.01f && bonusStaminaDecreased)
                    {
                        if (!MaxBonusWidths.ContainsKey(sb))
                        {
                            MaxBonusWidths[sb] = extraBarRect.rect.width;
                        }

                        float maxBonusWidth = MaxBonusWidths[sb];
                        if (extraBarRect.rect.width > maxBonusWidth)
                        {
                            maxBonusWidth = extraBarRect.rect.width;
                            MaxBonusWidths[sb] = maxBonusWidth;
                        }

                        float currentWidth = extraBarRect.rect.width;
                        float offset = (maxBonusWidth - currentWidth) / 2;

                        textRect.anchoredPosition = new Vector2(offset, _bonusTextOffsetY.Value);
                    }
                    else
                    {
                        MaxBonusWidths.Remove(sb);
                        textRect.anchoredPosition = new Vector2(0, _bonusTextOffsetY.Value);
                    }

                    UpdateText(bonusKey, bonusRawValue, !_displayBonusAsInteger.Value);
                }
                else
                {
                    UpdateText(bonusKey, bonusRawValue, !_displayBonusAsInteger.Value);
                }
            }
        }

        var afflictions = sb.GetComponentsInChildren<BarAffliction>(true);
        foreach (var affliction in afflictions)
        {
            string key = $"Affliction_{affliction.GetInstanceID()}";
            if (BarTexts.ContainsKey(key))
            {
                UpdateText(key, affliction.size, false);
            }
        }
    }

    private static void UpdateText(string key, float rawValue, bool showDecimal)
    {
        if (!BarTexts.TryGetValue(key, out var textMesh) || !LastKnownValues.TryGetValue(key, out var lastValue))
        {
            return;
        }

        if (textMesh == null)
        {
            BarTexts.Remove(key);
            LastKnownValues.Remove(key);
            return;
        }

        if (Mathf.Approximately(rawValue, lastValue))
        {
            return;
        }

        LastKnownValues[key] = rawValue;
        float displayValue = rawValue / _valueDivisor.Value;

        bool shouldDisplay = showDecimal ? displayValue > 0.5f : displayValue >= 1.0f;

        if (shouldDisplay)
        {
            textMesh.text = showDecimal ? displayValue.ToString("F1") : Mathf.FloorToInt(displayValue).ToString();
            textMesh.gameObject.SetActive(true);
        }
        else
        {
            textMesh.gameObject.SetActive(false);
        }
    }
}