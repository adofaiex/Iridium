using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Iris.Iml;
using Iridium.Patches;

namespace Iridium.UI
{
    public class VRAMNotificationUI : MonoBehaviour
    {
        private static VRAMNotificationUI? _instance;

        private IrisGoRenderer _renderer = null!;
        private CanvasGroup _canvasGroup = null!;
        private Coroutine? _fadeCoroutine;
        private Coroutine? _completeDelayCoroutine;

        // Exposed so FrameSpreadDecorationLoadingPatch can skip blocking it while loading.
        public static GraphicRaycaster? InstanceRaycaster { get; private set; }

        // Re-captured after each Rebuild so UpdateProgress can skip a full rebuild.
        private Text? _messageText;

        private string _message = "";
        private float _timer = 0f;
        private float _showStartTime = 0f;
        private bool _isPersistent = false;
        private const float FadeDuration = 0.5f;
        private const float DisplayDuration = 2.5f;
        // Minimum fully-visible time after ShowPersistent, so fast loads still show progress.
        private const float MinPersistentDisplay = 0.6f;

        public static void Show(string message)
        {
            EnsureInstance();
            // Drop a pending delayed-complete so it can't override this Show's timer.
            if (_instance._completeDelayCoroutine != null)
            {
                _instance.StopCoroutine(_instance._completeDelayCoroutine);
                _instance._completeDelayCoroutine = null;
            }
            _instance!._isPersistent = false;
            _instance!._timer = FadeDuration + DisplayDuration + FadeDuration;
            _instance!._SetContent(message, iconType: "success", showStop: false);
        }

        public static void ShowPersistent(string message)
        {
            EnsureInstance();
            // Drop a pending delayed Complete so the new UI gets a fresh display window.
            if (_instance!._completeDelayCoroutine != null)
            {
                _instance.StopCoroutine(_instance._completeDelayCoroutine);
                _instance._completeDelayCoroutine = null;
            }
            _instance!._isPersistent = true;
            _instance!._timer = float.MaxValue;
            _instance!._showStartTime = Time.realtimeSinceStartup;
            _instance!._SetContent(message, iconType: "information", showStop: true);
        }

        public static void UpdateProgress(string message)
        {
            if (_instance == null) return;
            _instance._message = message;
            _instance._isPersistent = true;
            _instance._timer = float.MaxValue;
            // The cached Text is invalidated by an external _SetContent/Rebuild and by child
            // reordering between rebuilds, so resolve it fresh each update (one GetChild + Find).
            _instance._SetMessageText(message);
        }

        /// <summary>
        /// Complete the persistent loading UI. When forceImmediate is false and less than
        /// MinPersistentDisplay seconds have elapsed since ShowPersistent, fade-out is
        /// delayed so fast loads still show progress.
        /// </summary>
        public static void Complete(bool forceImmediate = false)
        {
            if (_instance == null) return;
            // Drop the in-flight delay coroutine so we don't double-fade.
            if (_instance._completeDelayCoroutine != null)
            {
                _instance.StopCoroutine(_instance._completeDelayCoroutine);
                _instance._completeDelayCoroutine = null;
            }
            if (!forceImmediate)
            {
                float elapsedSinceShow = Time.realtimeSinceStartup - _instance._showStartTime;
                float wait = MinPersistentDisplay - elapsedSinceShow;
                if (wait > 0f)
                {
                    _instance._isPersistent = true;
                    _instance._timer = wait;
                    _instance._completeDelayCoroutine = _instance.StartCoroutine(_instance._CompleteAfterDelay(wait));
                    return;
                }
            }
            _instance._isPersistent = false;
            _instance._timer = FadeDuration + DisplayDuration + FadeDuration;
            _instance._StartFadeOut();
        }

        private IEnumerator _CompleteAfterDelay(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            _completeDelayCoroutine = null;
            if (_instance == null) yield break;
            _isPersistent = false;
            _timer = FadeDuration + DisplayDuration + FadeDuration;
            _StartFadeOut();
        }

        public static void RunCoroutine(IEnumerator routine)
        {
            EnsureInstance();
            _instance!.StartCoroutine(routine);
        }

        private static void EnsureInstance()
        {
            if (_instance == null)
            {
                var go = new GameObject("Iridium_VRAMNotification");
                _instance = go.AddComponent<VRAMNotificationUI>();
                DontDestroyOnLoad(go);
                _instance._Initialize();
            }
        }

        private void _Initialize()
        {
            // Own Canvas + CanvasGroup: skips the renderer's full-screen dark overlay,
            // fades via one CanvasGroup, and sits below ImlWindow's dialog (32766 vs 32767).
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0;
            _canvasGroup.blocksRaycasts = true;  // so the Stop button can receive clicks

            var canvasGo = new GameObject("VRAMNotificationCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32766;
            canvasGo.AddComponent<CanvasScaler>();
            InstanceRaycaster = canvasGo.AddComponent<GraphicRaycaster>();

            // Anchor at index 0: RebuildUI keeps it and destroys everything else, so a
            // previous-frame DialogWrapper would otherwise be preserved and leak.
            var placeholder = new GameObject("Placeholder");
            placeholder.transform.SetParent(canvasGo.transform, false);

            _renderer = new IrisGoRenderer
            {
                ParentTransform = transform,
                RootObject = canvasGo,
            };

            string imlPath = Path.Combine(
                Main.Handler?.ModPath ?? "",
                "Resources", "ui", "VRAMNotification.iml");

            if (File.Exists(imlPath))
            {
                _renderer.LoadFile(imlPath);
            }
            else
            {
                Main.Logger?.Log($"[VRAMNotificationUI] IML not found: {imlPath}");
            }

            _renderer.RegisterHandler("OnStop", (obj) =>
            {
                LoadingOptimizationPatches.FrameSpreadDecorationLoadingPatch.Cancel();
            });
        }

        private void _SetContent(string message, string iconType, bool showStop)
        {
            _message = message;

            _renderer.SetDataContext(new Dictionary<string, object>
            {
                ["message"] = message,
                ["iconType"] = iconType,
                ["showStop"] = showStop,
                ["stopText"] = Localization.Get("LoadingDecorationsStop"),
            });
            _renderer.Rebuild();

            // Small panel pinned top-left instead of the default screen center; also
            // re-caches refs, since Rebuild recreates the wrapper and its children.
            _PostProcessLayout();
            _CacheUIRefs();

            _StartFadeIn();
        }

        private void _PostProcessLayout()
        {
            if (_renderer.RootObject == null) return;
            // RebuildUI only queues the previous wrapper for end-of-frame Destroy, so the new
            // one is the LAST child: Transform.Find would return the stale pending-destroy one.
            int lastIdx = _renderer.RootObject.transform.childCount - 1;
            if (lastIdx < 0) return;
            var wrapper = _renderer.RootObject.transform.GetChild(lastIdx) as RectTransform;
            if (wrapper == null || wrapper.gameObject.name != "DialogWrapper") return;

            // Otherwise the layout pass overwrites sizeDelta with the HBox's preferred
            // width (~254px) and the wrapper oscillates between 254 and 400.
            DisableContentSizeFitter(wrapper);

            wrapper.anchorMin = new Vector2(0, 1);
            wrapper.anchorMax = new Vector2(0, 1);
            wrapper.pivot = new Vector2(0, 1);
            wrapper.anchoredPosition = new Vector2(20, -20);
            wrapper.sizeDelta = new Vector2(400, 50);

            // Strip the HBox's ContentSizeFitter and LayoutElement: either one drives its
            // RectTransform from preferred sizes, making it far taller than the 50px
            // wrapper (text wraps to one char per line). Let the anchor govern it.
            var hbox = wrapper.Find("HBox") as RectTransform;
            if (hbox != null)
            {
                DisableContentSizeFitter(hbox);
                var le = hbox.GetComponent<LayoutElement>();
                if (le != null) UnityEngine.Object.Destroy(le);

                // childForceExpandHeight would stretch children into tall pills inside a 50px wrapper.
                var hlg = hbox.GetComponent<HorizontalLayoutGroup>();
                if (hlg != null)
                {
                    hlg.childForceExpandHeight = false;
                    hlg.childControlHeight = true;
                    hlg.childAlignment = TextAnchor.MiddleLeft;
                }

                // Re-assert the stretch anchor post-BuildContainer.
                hbox.anchorMin = Vector2.zero;
                hbox.anchorMax = Vector2.one;
                hbox.offsetMin = Vector2.zero;
                hbox.offsetMax = Vector2.zero;
                hbox.sizeDelta = Vector2.zero; // (anchor stretch) → fill wrapper

                // Panel background stays click-through; the Stop button keeps raycastTarget.
                var img = hbox.GetComponent<Image>();
                if (img != null) img.raycastTarget = false;

                // BuildButton already sets these, but a silent flip here means dead clicks.
                var btn = hbox.Find("Button");
                if (btn != null)
                {
                    var btnComp = btn.GetComponent<Button>();
                    if (btnComp != null) btnComp.enabled = true;
                    var btnImg = btn.GetComponent<Image>();
                    if (btnImg != null) btnImg.raycastTarget = true;
                }
            }

            // Apply the stripped LayoutElement and reassigned anchor this frame.
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(wrapper);
        }

        private static void DisableContentSizeFitter(RectTransform rt)
        {
            if (rt == null) return;
            var csf = rt.GetComponent<ContentSizeFitter>();
            if (csf == null) return;
            csf.enabled = false;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            csf.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        private void _CacheUIRefs()
        {
            _messageText = null;
            if (_renderer.RootObject == null) return;
            // Same caveat as _PostProcessLayout: the previous-frame Text is still
            // in the hierarchy (pending destroy) until end of frame, so we must
            // walk only the new wrapper (last child of RootObject) to find the
            // Text that UpdateProgress should actually write to.
            int lastIdx = _renderer.RootObject.transform.childCount - 1;
            if (lastIdx < 0) return;
            var wrapper = _renderer.RootObject.transform.GetChild(lastIdx);
            if (wrapper == null || wrapper.gameObject.name != "DialogWrapper") return;
            // The renderer names the UGUI Text GameObject "Text".
            var txt = wrapper.Find("HBox/Text");
            if (txt != null) _messageText = txt.GetComponent<Text>();
        }

        /// <summary>Re-resolves the live Text each call (one GetChild + Find) so an external Rebuild can't leave us writing to a destroyed Text.</summary>
        private void _SetMessageText(string message)
        {
            if (_renderer?.RootObject == null) return;
            int lastIdx = _renderer.RootObject.transform.childCount - 1;
            if (lastIdx < 0) return;
            var wrapper = _renderer.RootObject.transform.GetChild(lastIdx);
            if (wrapper == null || wrapper.gameObject.name != "DialogWrapper") return;
            var txt = wrapper.Find("HBox/Text");
            if (txt == null) return;
            var textComp = txt.GetComponent<Text>();
            if (textComp == null) return;
            textComp.text = message;
            // Keep the cache in sync for other readers of _messageText.
            _messageText = textComp;
        }

        private void _StartFadeIn()
        {
            // Snap to full alpha: a fade-in gets cut off by Complete at low alpha
            // when loading finishes in under FadeDuration.
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _canvasGroup.alpha = 1f;
            _fadeCoroutine = null;
        }

        private void _StartFadeOut()
        {
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            if (_canvasGroup.alpha <= 0.001f)
            {
                _fadeCoroutine = null;
                return;
            }
            _fadeCoroutine = StartCoroutine(_FadeCanvasGroup(_canvasGroup.alpha, 0, FadeDuration));
        }

        private IEnumerator _FadeCanvasGroup(float from, float to, float duration)
        {
            float elapsed = 0;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(from, to, elapsed / duration);
                yield return null;
            }
            _canvasGroup.alpha = to;
            _fadeCoroutine = null;
        }

        private void Update()
        {
            if (_timer > 0f && !_isPersistent) _timer -= Time.deltaTime;
            if (_timer <= 0f && !_isPersistent && _fadeCoroutine == null)
            {
                _StartFadeOut();
            }
        }
    }
}
