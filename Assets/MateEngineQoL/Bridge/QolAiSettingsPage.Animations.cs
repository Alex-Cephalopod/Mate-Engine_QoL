using System;
using System.Collections.Generic;
using MateEngineQoL.Animations;
using MateEngineQoL.Settings;
using MateEngineQoL.Tags;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace MateEngineQoL.Bridge
{
    /// <summary>The ANIMATIONS section (Phase 4 library panel): idles on/off, the clips, preview, favourite, set as idle.</summary>
    public sealed partial class QolAiSettingsPage
    {
        TMP_Dropdown _animIdles, _animTags, _animClip;
        Button _animFavourite, _animPin;
        TMP_Text _animStatus;
        readonly List<string> _animClipIds = new List<string>();
        readonly List<CanvasGroup> _animClipRows = new List<CanvasGroup>();
        bool _animSubscribed;

        void BuildAnimationRows(Transform content, TMP_Text titleTemplate, TMP_Dropdown dropdownTemplate, TMP_Text noteTemplate,
            Button buttonTemplate)
        {
            if (titleTemplate != null) AddText(content, titleTemplate, QolText.Get("QOL_ANIM_TITLE"), 60f);

            _animIdles = AddDropdown(content, dropdownTemplate, "QOL_ANIM_IDLES");
            _animIdles.ClearOptions();
            _animIdles.AddOptions(new List<string> { QolText.Get("QOL_ANIM_ON"), QolText.Get("QOL_ANIM_OFF") });
            _animIdles.onValueChanged.AddListener(i =>
            {
                QolSettings s = QolServices.Settings;
                s.Animation.LibraryIdles = i == 0;
                QolServices.SaveSettings(s);
                RefreshAnimations();
            });

            _animTags = AddDropdown(content, dropdownTemplate, "QOL_ANIM_TAGS");
            _animTags.ClearOptions();
            _animTags.AddOptions(new List<string> { QolText.Get("QOL_ANIM_ON"), QolText.Get("QOL_ANIM_OFF") });
            _animTags.onValueChanged.AddListener(i =>
            {
                QolSettings s = QolServices.Settings;
                s.Tags.Enabled = i == 0;
                QolServices.SaveSettings(s);
                RefreshAnimationStatus();
            });

            _animClip = AddDropdown(content, dropdownTemplate, "QOL_ANIM_CLIP");
            _animClipRows.Add(_animClip.transform.parent.gameObject.AddComponent<CanvasGroup>());
            _animClip.onValueChanged.AddListener(_ => RefreshAnimationButtons());

            Button[] clipButtons = AddButtonRow(content, buttonTemplate,
                ("QOL_ANIM_PREVIEW", () => { if (SelectedClip() is string id) QolAnimationLibrary.Instance?.Preview(id); RefreshAnimationStatus(); }),
                ("QOL_ANIM_FAVOURITE", ToggleFavourite),
                ("QOL_ANIM_SET_IDLE", TogglePin));
            _animFavourite = clipButtons[1];
            _animPin = clipButtons[2];
            _animClipRows.Add(clipButtons[0].transform.parent.gameObject.AddComponent<CanvasGroup>());

            AddButtonRow(content, buttonTemplate,
                ("QOL_ANIM_RELOAD", () => { QolAnimationLibrary.Instance?.Reload(); RefreshAnimations(); }),
                ("QOL_ANIM_OPEN", OpenAnimationFolder));

            _animStatus = AddText(content, noteTemplate, "", 40f);
            AddText(content, noteTemplate, QolText.Get("QOL_ANIM_NOTE"), 40f);
        }

        void RefreshAnimations()
        {
            // The library installs at the same moment as this page, in no set order, so subscribe on first use.
            if (!_animSubscribed && QolAnimationLibrary.Instance != null)
            {
                _animSubscribed = true;
                QolAnimationLibrary.Instance.CatalogChanged += () => { if (this != null && isActiveAndEnabled) RefreshAnimations(); };
            }

            AnimationSettings a = QolServices.Settings.Animation;
            _animIdles.SetValueWithoutNotify(a.LibraryIdles ? 0 : 1);
            _animIdles.RefreshShownValue();
            _animTags.SetValueWithoutNotify(QolServices.Settings.Tags.Enabled ? 0 : 1);
            _animTags.RefreshShownValue();

            string selected = SelectedClip();
            _animClipIds.Clear();
            var labels = new List<string>();
            AnimationCatalog catalog = QolAnimationLibrary.Instance?.Catalog;
            if (catalog != null)
            {
                var entries = new List<AnimationEntry>(catalog.Entries);
                // Favourites first, then by category, then by name.
                entries.Sort((x, y) =>
                {
                    int f = IsFavourite(y.Id).CompareTo(IsFavourite(x.Id));
                    if (f != 0) return f;
                    int c = string.Compare(x.Info.Category, y.Info.Category, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : string.Compare(x.Info.Name, y.Info.Name, StringComparison.OrdinalIgnoreCase);
                });
                foreach (AnimationEntry e in entries)
                {
                    _animClipIds.Add(e.Id);
                    labels.Add((IsFavourite(e.Id) ? "★ " : "") + e.Info.Name + "  (" + e.Info.Category + ")");
                }
            }
            bool any = _animClipIds.Count > 0;
            if (!any) labels.Add(QolText.Get("QOL_ANIM_NONE"));
            _animClip.ClearOptions();
            _animClip.AddOptions(labels);
            int index = selected != null ? _animClipIds.FindIndex(id => string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)) : -1;
            _animClip.SetValueWithoutNotify(Mathf.Max(0, index));
            _animClip.RefreshShownValue();

            foreach (CanvasGroup g in _animClipRows)
            {
                g.interactable = any;
                g.alpha = any ? 1f : 0.4f;
            }
            RefreshAnimationButtons();
        }

        void RefreshAnimationButtons()
        {
            string id = SelectedClip();
            SetButtonLabel(_animFavourite.gameObject, id != null && IsFavourite(id) ? "QOL_ANIM_UNFAVOURITE" : "QOL_ANIM_FAVOURITE", 0f);
            string pinned = QolServices.Settings.Animation.PinnedIdle;
            bool isPinned = id != null && string.Equals(id, pinned, StringComparison.OrdinalIgnoreCase);
            SetButtonLabel(_animPin.gameObject, isPinned ? "QOL_ANIM_ROTATE" : "QOL_ANIM_SET_IDLE", 0f);
            RefreshAnimationStatus();
        }

        void RefreshAnimationStatus()
        {
            QolAnimationLibrary lib = QolAnimationLibrary.Instance;
            if (lib == null || lib.Catalog == null)
            {
                _animStatus.text = QolText.Get("QOL_ANIM_UNAVAILABLE");
                return;
            }
            int idles = 0;
            foreach (AnimationEntry e in lib.Catalog.Entries) if (e.IsIdle) idles++;
            string status = QolText.Format("QOL_ANIM_STATUS", lib.Catalog.Entries.Count, idles, lib.Folder);
            if (QolServices.Settings.Tags.Enabled)
            {
                List<string> actions = ReplyTagVocabulary.ActionNames(lib.Catalog.Entries);
                status += " " + (actions.Count > 0
                    ? QolText.Format("QOL_ANIM_ACTIONS", string.Join(", ", actions))
                    : QolText.Get("QOL_ANIM_NO_ACTIONS"));
            }
            string pinned = QolServices.Settings.Animation.PinnedIdle;
            if (pinned != null) status += " " + QolText.Format("QOL_ANIM_PINNED", lib.Catalog.Find(pinned)?.Info.Name ?? pinned);
            if (lib.LastError != null) status += " " + QolText.Format("QOL_ANIM_ERROR", lib.LastError);
            _animStatus.text = status;
        }

        string SelectedClip()
        {
            if (_animClip == null) return null;
            int i = _animClip.value;
            return i >= 0 && i < _animClipIds.Count ? _animClipIds[i] : null;
        }

        static bool IsFavourite(string id)
        {
            foreach (string f in QolServices.Settings.Animation.Favourites)
                if (string.Equals(f, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void ToggleFavourite()
        {
            string id = SelectedClip();
            if (id == null) return;
            QolSettings s = QolServices.Settings;
            if (IsFavourite(id)) s.Animation.Favourites.RemoveAll(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase));
            else s.Animation.Favourites.Add(id);
            QolServices.SaveSettings(s);
            RefreshAnimations();
        }

        void TogglePin()
        {
            string id = SelectedClip();
            QolAnimationLibrary lib = QolAnimationLibrary.Instance;
            if (id == null || lib == null) return;
            bool isPinned = string.Equals(id, QolServices.Settings.Animation.PinnedIdle, StringComparison.OrdinalIgnoreCase);
            lib.PinIdle(isPinned ? null : id);
            RefreshAnimationButtons();
        }

        void OpenAnimationFolder()
        {
            string folder = QolAnimationLibrary.Instance?.Folder;
            if (string.IsNullOrEmpty(folder)) return;
            try
            {
                System.IO.Directory.CreateDirectory(folder);
#if UNITY_STANDALONE_WIN
                // Our own data folder, never text from a model or the network (docs/SECURITY.md).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = false });
#else
                Application.OpenURL(new Uri(folder).AbsoluteUri);
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning("[QoL Animation] Can't open the folder: " + e.Message);
            }
        }

        /// <summary>Buttons side by side in one row, splitting its width.</summary>
        Button[] AddButtonRow(Transform content, Button template, params (string Key, UnityEngine.Events.UnityAction OnClick)[] buttons)
        {
            const float height = 44f, gap = 8f;
            RectTransform row = Row(content, height);
            var result = new Button[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                GameObject go = Clone(template.gameObject, row);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = new Vector2((float)i / buttons.Length, 1f);
                rt.anchorMax = new Vector2((float)(i + 1) / buttons.Length, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.offsetMin = new Vector2(i == 0 ? 0f : gap / 2f, -RowPadding - height);
                rt.offsetMax = new Vector2(i == buttons.Length - 1 ? 0f : -gap / 2f, -RowPadding);
                SetButtonLabel(go, buttons[i].Key, 0f);
                result[i] = go.GetComponent<Button>();
                result[i].onClick.AddListener(buttons[i].OnClick);
            }
            return result;
        }
    }
}
