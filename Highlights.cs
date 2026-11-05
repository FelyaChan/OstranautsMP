using System.Collections.Generic;
using UnityEngine;

namespace OstranautsMP
{
	// Optional (Multiplayer window, "Highlights"):
	// - your own locker (Ownership) lights up in your ping colour while the mouse is over it;
	// - the other player's character has a faint aura in their ping colour while your character can see them (the
	//   game's own line of sight: walls and closed doors block it, glass doesn't). It fades in and out.
	//
	// Protocol: PCOLOR <hex RRGGBB>     both ways, on connecting and when the colour changes
	public static class Highlights
	{
		private const float LosInterval = 0.2f;

		// Aura strength at its fullest, and how fast it fades in or out (per second).
		private const float AuraAlpha = 0.22f;

		private const float FadeSpeed = 2.5f;

		private static Texture2D _glow;

		private static Texture2D _white;

		private static string _sentColor;

		private static Color _peerColor = new Color(0.4f, 0.8f, 1f);

		private static CondOwner _hoverLocker;

		private static float _nextLos;

		private static bool _seen;

		private static float _aura;

		private static CondOwner Mine => (Plugin.Net.CurrentMode == NetSession.Mode.Host) ? CrewSim.coPlayer : CrewClaim.ClientMine;

		private static CondOwner Theirs => (Plugin.Net.CurrentMode == NetSession.Mode.Host) ? CrewClaim.HostClaimed : CrewSim.coPlayer;

		public static void Update(float now)
		{
			if (!Plugin.Net.Connected)
			{
				_sentColor = null;
				_hoverLocker = null;
				_seen = false;
				_aura = 0f;
				return;
			}
			if (Plugin.MarkColorHex != _sentColor)
			{
				_sentColor = Plugin.MarkColorHex;
				Plugin.Net.Send("PCOLOR " + _sentColor);
			}
			if (!WorldSync.InGame || !Plugin.HighlightsOn)
			{
				_hoverLocker = null;
				_seen = false;
				_aura = 0f;
				return;
			}
			_hoverLocker = HoveredLocker();
			if (now >= _nextLos)
			{
				_nextLos = now + LosInterval;
				_seen = CanSee();
			}
			_aura = Mathf.MoveTowards(_aura, _seen ? 1f : 0f, FadeSpeed * Time.unscaledDeltaTime);
		}

		private static CondOwner HoveredLocker()
		{
			CondOwner me = Mine;
			if (me == null || Chat.MouseOverModUI)
			{
				return null;
			}
			List<CondOwner> under;
			try
			{
				under = CrewSim.objInstance.GetMouseOverCOsExternal();
			}
			catch
			{
				return null;
			}
			if (under == null)
			{
				return null;
			}
			foreach (CondOwner co in under)
			{
				if (co != null && !co.bDestroyed && Ownership.OwnedBy(co, me))
				{
					return co;
				}
			}
			return null;
		}

		private static bool CanSee()
		{
			CondOwner me = Mine;
			CondOwner them = Theirs;
			if (me == null || them == null || me.bDestroyed || them.bDestroyed || me.ship == null || them.ship == null || them.tf == null || !them.Visible)
			{
				return false;
			}
			try
			{
				return Visibility.IsCondOwnerLOSVisibleBlocks(them, me.tf.position, bIgnoreEndpoints: false, bIgnoreGlass: true);
			}
			catch
			{
				return false;
			}
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "PCOLOR")
			{
				return false;
			}
			Color c;
			if (ColorUtility.TryParseHtmlString("#" + arg, out c))
			{
				_peerColor = c;
			}
			return true;
		}

		// ---- Drawing ----

		public static void OnGUI()
		{
			if (Event.current.type != EventType.Repaint || !Plugin.Net.Connected || !WorldSync.InGame || !Plugin.HighlightsOn)
			{
				return;
			}
			Camera cam = (CrewSim.objInstance != null) ? CrewSim.objInstance.camMain : null;
			if (cam == null)
			{
				return;
			}
			EnsureTextures();
			if (_hoverLocker != null && !_hoverLocker.bDestroyed)
			{
				DrawLocker(cam, _hoverLocker, Plugin.MarkColor);
			}
			CondOwner them = Theirs;
			if (_aura > 0.001f && them != null && !them.bDestroyed && them.tf != null)
			{
				DrawAura(cam, them, _peerColor, _aura);
			}
		}

		private static void DrawLocker(Camera cam, CondOwner co, Color color)
		{
			Renderer r = co.GetComponent<Renderer>();
			if (r == null)
			{
				return;
			}
			Bounds b = r.bounds;
			Vector3 a = cam.WorldToScreenPoint(new Vector3(b.min.x, b.min.y, b.center.z));
			Vector3 z = cam.WorldToScreenPoint(new Vector3(b.max.x, b.max.y, b.center.z));
			Rect rect = Rect.MinMaxRect(Mathf.Min(a.x, z.x), Screen.height - Mathf.Max(a.y, z.y), Mathf.Max(a.x, z.x), Screen.height - Mathf.Min(a.y, z.y));
			float pulse = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 4f);
			Color glow = color;
			glow.a = 0.35f * pulse;
			float pad = Mathf.Max(rect.width, rect.height) * 0.35f;
			Draw(new Rect(rect.x - pad, rect.y - pad, rect.width + pad * 2f, rect.height + pad * 2f), _glow, glow);
			Color line = color;
			line.a = 0.9f * pulse;
			const float w = 2f;
			Draw(new Rect(rect.x, rect.y, rect.width, w), _white, line);
			Draw(new Rect(rect.x, rect.yMax - w, rect.width, w), _white, line);
			Draw(new Rect(rect.x, rect.y, w, rect.height), _white, line);
			Draw(new Rect(rect.xMax - w, rect.y, w, rect.height), _white, line);
		}

		private static void DrawAura(Camera cam, CondOwner co, Color color, float strength)
		{
			Vector3 p = cam.WorldToScreenPoint(co.tf.position);
			if (p.z < 0f)
			{
				return;
			}
			Renderer r = co.GetComponent<Renderer>();
			float radius = 0.5f;
			if (r != null)
			{
				radius = Mathf.Max(r.bounds.extents.x, r.bounds.extents.y);
			}
			Vector3 edge = cam.WorldToScreenPoint(co.tf.position + Vector3.right * radius);
			float size = Mathf.Max(24f, Mathf.Abs(edge.x - p.x)) * 3.2f;
			Color c = color;
			c.a = AuraAlpha * strength * (0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 2f));
			Draw(new Rect(p.x - size / 2f, Screen.height - p.y - size / 2f, size, size), _glow, c);
		}

		private static void Draw(Rect r, Texture2D tex, Color c)
		{
			Color saved = GUI.color;
			GUI.color = c;
			GUI.DrawTexture(r, tex);
			GUI.color = saved;
		}

		private static void EnsureTextures()
		{
			if (_white == null)
			{
				_white = new Texture2D(1, 1);
				_white.SetPixel(0, 0, Color.white);
				_white.Apply();
				_white.hideFlags = HideFlags.HideAndDontSave;
			}
			if (_glow == null)
			{
				const int n = 64;
				_glow = new Texture2D(n, n, TextureFormat.RGBA32, false);
				_glow.wrapMode = TextureWrapMode.Clamp;
				_glow.hideFlags = HideFlags.HideAndDontSave;
				for (int y = 0; y < n; y++)
				{
					for (int x = 0; x < n; x++)
					{
						float dx = (x + 0.5f) / n * 2f - 1f;
						float dy = (y + 0.5f) / n * 2f - 1f;
						float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
						float a = (1f - d) * (1f - d);
						_glow.SetPixel(x, y, new Color(1f, 1f, 1f, a));
					}
				}
				_glow.Apply();
			}
		}
	}
}
