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

        // The notification's own GraphicRaycaster. FrameSpreadDecorationLoadingPatch
        // blocks ALL raycasters while loading — including this one — which makes the
        // Stop button unclickable. Expose it so the loading patch can skip it.
        public static GraphicRaycaster? InstanceRaycaster { get; private set; }

        // Cached references — captured right after each Rebuild so UpdateProgress
        // can change text without rebuilding the whole UI tree.
        private Text? _messageText;

        private string _message = "";
        private float _timer = 0f;
        private float _showStartTime = 0f;
        private bool _isPersistent = false;
        private const float FadeDuration = 0.5f;
        private const float DisplayDuration = 2.5f;
        // Minimum time the persistent UI must stay fully visible after ShowPersistent,
        // so that fast loads (< 0.5s) still show the progress to the player instead of
        // fading in / out at low alpha while the screen is still black.
        private const float MinPersistentDisplay = 0.6f;

        public static void Show(string message)
        {
            EnsureInstance();
            // Cancel any pending delayed-complete so it can't override this Show's
            // fade timer afterwards.
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
            // Cancel any pending delayed Complete from a previous Show so the new
            // persistent UI gets a fresh minimum-display window.
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
            // Re-resolve the Text component on every progress update instead of
            // trusting the cached reference. The OLD IMGUI implementation
            // (commit 538c294^) read `_message` fresh in OnGUI() every frame —
            // the new UGUI/IML path cannot rely on a single cached Text
            // reference because:
            //   (a) _SetContent can be called from outside (e.g. OptimizerPatches'
            //       VRAMNotificationPatch.Postfix → Show), which Rebuilds the
            //       UI and leaves the cached reference pointing at a destroyed
            //       Text component, and
            //   (b) the wrapper's child ordering can shift between rebuilds
            //       (pending-destroy wrappers vs new wrapper), making the
            //       cached _messageText point at a stale object.
            // Walking the hierarchy each call is cheap (one GetChild + Find)
            // and matches the "always-read-the-latest-value" semantics of the
            // old IMGUI version, which never had this "0/129 stuck" problem.
            _instance._SetMessageText(message);
        }

        /// <summary>
        /// Complete the persistent loading UI. If <paramref name="forceImmediate"/> is
        /// false and less than <see cref="MinPersistentDisplay"/> seconds have elapsed
        /// since <see cref="ShowPersistent"/>, the fade-out is delayed so the user
        /// actually sees the progress for fast loads.
        /// </summary>
        public static void Complete(bool forceImmediate = false)
        {
            if (_instance == null) return;
            // Cancel any in-flight delay coroutine so we don't double-fade.
            if (_instance._completeDelayCoroutine != null)
            {
                _instance.StopCoroutine(_instance._completeDelayCoroutine);
                _instance._completeDelayCoroutine = null;
            }
            if (!forceImmediate)
            {
                // Enforce a minimum visible time for the persistent UI so that fast loads
                // (where ShowPersistent was called < MinPersistentDisplay seconds ago) still
                // get a chance to display the progress to the player before fading out.
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
            // Build our own Canvas + CanvasGroup so we can:
            //   1. avoid the renderer's full-screen dark overlay (created in EnsureRoot)
            //   2. fade the entire notification via a single CanvasGroup
            //   3. sit below ImlWindow's dialog (sortingOrder 32767 vs 32766)
            // Pre-creating RootObject + a placeholder also makes the renderer's
            // "keep child at index 0" loop behave correctly across multiple rebuilds.
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

            // Placeholder: the renderer's RebuildUI loop destroys all children except
            // index 0. Without this anchor, a previous-frame DialogWrapper would be
            // preserved as index 0 and leak each time _SetContent is called.
            var placeholder = new GameObject("Placeholder");
            placeholder.transform.SetParent(canvasGo.transform, false);

            _renderer = new IrisGoRenderer
            {
                ParentTransform = transform,
                RootObject = canvasGo,
                // Fixed panel size: the shared layout engine lays the root out at
                // exactly this size (Fill grows to fill it), and the wrapper is
                // sized to match — _PostProcessLayout only pins the position.
                RootSizeOverride = new Vector2(400, 50),
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

            // Renderer positions the DialogWrapper at screen center by default; we
            // want a small panel pinned to the top-left. Also re-cache refs (the
            // wrapper + children are recreated on every Rebuild).
            _PostProcessLayout();
            _CacheUIRefs();

            _StartFadeIn();
        }

        private void _PostProcessLayout()
        {
            if (_renderer.RootObject == null) return;
            // IMPORTANT: the renderer's RebuildUI loop only queues the previous-frame
            // wrapper for destruction (Destroy is end of frame). The new wrapper is
            // appended to the end of the children list, so we must pick the LAST
            // child — Transform.Find("DialogWrapper") would otherwise return the
            // old pending-destroy wrapper.
            int lastIdx = _renderer.RootObject.transform.childCount - 1;
            if (lastIdx < 0) return;
            var wrapper = _renderer.RootObject.transform.GetChild(lastIdx) as RectTransform;
            if (wrapper == null || wrapper.gameObject.name != "DialogWrapper") return;

            // Pin to top-left. Size comes from the renderer (RootSizeOverride),
            // and children rects are laid out by the shared layout engine — no
            // ContentSizeFitter / LayoutGroup surgery needed anymore.
            wrapper.anchorMin = new Vector2(0, 1);
            wrapper.anchorMax = new Vector2(0, 1);
            wrapper.pivot = new Vector2(0, 1);
            wrapper.anchoredPosition = new Vector2(20, -20);
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

        /// <summary>
        /// Walk the live UI tree to find the message Text and set its text.
        /// Mirrors the OLD IMGUI behavior of re-reading <c>_message</c> in
        /// OnGUI() every frame, but for UGUI: instead of trusting the cached
        /// <c>_messageText</c> reference (which can be invalidated by an
        /// external Rebuild), re-resolve the Text each call. Cheap (one
        /// GetChild + Find) and immune to stale-reference issues.
        /// </summary>
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
            // Refresh the cached reference too — keeps _CacheUIRefs's result
            // consistent with what we just wrote, in case anyone else reads
            // _messageText.
            _messageText = textComp;
        }

        private void _StartFadeIn()
        {
            // Stop any in-flight fade so the persistent UI snaps fully visible
            // (Complete uses _StartFadeOut to fade back down). A 0.5s fade-in was
            // tried originally, but when frame-spread loading finishes in <0.5s
            // (typical for levels with 100-1000 decorations) the fade-in gets
            // interrupted by Complete at low alpha and the user sees nothing.
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
