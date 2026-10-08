using System;
using UnityEngine;

namespace MateEngineQoL.Bridge
{
    /// <summary>Upstream moments QoL code reacts to. Raised from one-line hooks marked "// QoL:" in upstream files.</summary>
    public static class QolEvents
    {
        /// <summary>A model became the active avatar (custom VRM / .me loaded, or the default model re-enabled).</summary>
        public static event Action<GameObject> AvatarLoaded;

        public static void RaiseAvatarLoaded(GameObject model)
        {
            if (model == null || AvatarLoaded == null) return;
            // One failing listener must not break upstream's load path.
            foreach (Action<GameObject> listener in AvatarLoaded.GetInvocationList())
            {
                try { listener(model); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
