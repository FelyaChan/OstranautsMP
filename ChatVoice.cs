using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace OstranautsMP
{
	// Speech sounds: each letter of a speech bubble's text, as it's typed out, plays a short synthesized blip from the
	// speaker. 3D like the game's own world sounds (quieter far away and off screen), through the game's Ambient
	// mixer group, so the atmosphere acts on it as on everything else (muffled in a helmet, nothing in vacuum), and the
	// effects volume in the game's options sets its loudness.
	//
	// The sound: a triangle wave for vowels, a square for the rest, gliding slightly down, low-pass filtered, with a
	// sine an octave up for ring, through a soft tanh drive. The note comes from the letter (a pentatonic step from
	// G3), so any phrase stays in tune. 7 steps x vowel / consonant = 14 clips, made once; the speaker's voice pitch
	// (semitones, from their profile) and a little randomness are the AudioSource's pitch.
	//
	// Radio (the speaker in a helmet with a charged suit battery): the same letters through a narrow band with a
	// little hiss and harder drive, flat (2D) and outside the mixer since it's in your own helmet, heard anywhere; a
	// short burst of static when the line starts and when it ends.
	//
	// Nothing is sent for this: every bubble carries its speaker's voice (Chat, SAY), and each side plays it.
	public static class ChatVoice
	{
		private const int Rate = 44100;

		private const float BaseHz = 196f;

		private const float Drive = 2.5f;

		// Overall loudness against the game's own sounds.
		private const float Volume = 0.35f;

		// The game's world sounds: full within 20, nothing past 50.
		private const float MinDistance = 20f;

		private const float MaxDistance = 50f;

		private static readonly int[] Scale = { 0, 2, 4, 7, 9, 12, 14 };

		private const string Vowels = "aeiouyаеёиоуыэюя";

		private static AudioClip[] _vowel;

		private static AudioClip[] _consonant;

		private static AudioClip[] _radioVowel;

		private static AudioClip[] _radioConsonant;

		private static AudioClip _radioClick;

		private static AudioSource _radioSource;

		private static bool _failed;

		// Whether the Ambient group sits under the effects volume in the game's mixer; if not, we scale by it.
		private static bool? _underEffects;

		private static readonly Dictionary<string, AudioSource> Sources = new Dictionary<string, AudioSource>();

		// One letter of what characterId says, in their voice.
		internal static void Play(string characterId, char ch, float pitchSemitones, bool radio)
		{
			if (!Plugin.SpeechEffectsOn || _failed || string.IsNullOrEmpty(characterId) || !char.IsLetterOrDigit(ch))
			{
				return;
			}
			if (radio)
			{
				PlayRadio(ch, pitchSemitones);
				return;
			}
			CondOwner co;
			if (!DataHandler.mapCOs.TryGetValue(characterId, out co) || co == null || co.bDestroyed || co.tf == null)
			{
				return;
			}
			try
			{
				AudioSource src = SourceFor(characterId, co);
				if (src != null)
				{
					PlayOn(src, ch, pitchSemitones, Volume * EffectsScale());
				}
			}
			catch (Exception e)
			{
				_failed = true;
				Plugin.Log.LogError("Speech sounds off: " + e);
			}
		}

		private static void PlayRadio(char ch, float pitchSemitones)
		{
			try
			{
				AudioSource src = RadioSource();
				PlayOn(src, ch, pitchSemitones, RadioVolume(), radio: true);
			}
			catch (Exception e)
			{
				_failed = true;
				Plugin.Log.LogError("Speech sounds off: " + e);
			}
		}

		// The static at the start and end of a radio line.
		internal static void RadioClick()
		{
			if (!Plugin.SpeechEffectsOn || _failed)
			{
				return;
			}
			try
			{
				if (_vowel == null)
				{
					Build();
				}
				AudioSource src = RadioSource();
				src.pitch = 1f;
				src.volume = RadioVolume();
				src.PlayOneShot(_radioClick, ClickLevel);
			}
			catch (Exception e)
			{
				_failed = true;
				Plugin.Log.LogError("Speech sounds off: " + e);
			}
		}

		// The static burst against the voice.
		private const float ClickLevel = 0.315f;

		private static float RadioVolume()
		{
			return Volume * Mathf.Clamp01(PlayerPrefs.GetFloat("fVolEffects", 1f));
		}

		private static AudioSource RadioSource()
		{
			if (_radioSource == null)
			{
				GameObject go = new GameObject("MPRadioVoice");
				UnityEngine.Object.DontDestroyOnLoad(go);
				_radioSource = go.AddComponent<AudioSource>();
				_radioSource.playOnAwake = false;
				_radioSource.spatialBlend = 0f;
			}
			return _radioSource;
		}

		private static void PlayOn(AudioSource src, char ch, float pitchSemitones, float volume, bool radio = false)
		{
			if (_vowel == null)
			{
				Build();
			}
			char lower = char.ToLowerInvariant(ch);
			int step = lower % Scale.Length;
			bool vowel = Vowels.IndexOf(lower) >= 0;
			AudioClip clip = radio ? (vowel ? _radioVowel[step] : _radioConsonant[step]) : (vowel ? _vowel[step] : _consonant[step]);
			src.pitch = Mathf.Pow(2f, pitchSemitones / 12f) * (1f + UnityEngine.Random.Range(-0.015f, 0.015f));
			src.volume = volume;
			src.PlayOneShot(clip);
		}

		// The Listen button in the profile's Voice tab: a sample phrase typed out at the given voice, flat (2D) and
		// outside the mixer, so it sounds the same in the menu and in game; the effects volume still sets its loudness.
		private const string PreviewText = "Hello! This is how I sound.";

		private static AudioSource _previewSource;

		private static bool _previewing;

		private static float _previewStart;

		private static float _previewPitch;

		private static float _previewSpeed;

		private static int _previewDone;

		internal static void Preview(float pitchSemitones, float speed)
		{
			_previewing = true;
			_previewStart = Time.unscaledTime;
			_previewPitch = pitchSemitones;
			_previewSpeed = speed;
			_previewDone = 0;
		}

		// Every frame (Plugin.Update); voices the newest letter, as Chat does.
		internal static void UpdatePreview()
		{
			if (!_previewing || _failed)
			{
				return;
			}
			int n = Mathf.Min(PreviewText.Length, Mathf.FloorToInt((Time.unscaledTime - _previewStart) * Chat.TypeCharsPerSecond * _previewSpeed));
			if (n <= _previewDone)
			{
				return;
			}
			_previewDone = n;
			if (n >= PreviewText.Length)
			{
				_previewing = false;
			}
			char ch = PreviewText[n - 1];
			if (!char.IsLetterOrDigit(ch))
			{
				return;
			}
			try
			{
				if (_previewSource == null)
				{
					GameObject go = new GameObject("MPSpeechPreview");
					UnityEngine.Object.DontDestroyOnLoad(go);
					_previewSource = go.AddComponent<AudioSource>();
					_previewSource.playOnAwake = false;
					_previewSource.spatialBlend = 0f;
				}
				PlayOn(_previewSource, ch, _previewPitch, Volume * Mathf.Clamp01(PlayerPrefs.GetFloat("fVolEffects", 1f)));
			}
			catch (Exception e)
			{
				_failed = true;
				_previewing = false;
				Plugin.Log.LogError("Speech sounds off: " + e);
			}
		}

		// A source on the speaker, so it follows them.
		private static AudioSource SourceFor(string characterId, CondOwner co)
		{
			AudioSource src;
			if (Sources.TryGetValue(characterId, out src) && src != null && src.transform.parent == co.tf)
			{
				return src;
			}
			GameObject go = new GameObject("MPSpeechVoice");
			go.transform.SetParent(co.tf, false);
			src = go.AddComponent<AudioSource>();
			src.playOnAwake = false;
			src.spatialBlend = 1f;
			src.dopplerLevel = 0f;
			src.rolloffMode = AudioRolloffMode.Linear;
			src.minDistance = MinDistance;
			src.maxDistance = MaxDistance;
			AudioMixerGroup group;
			if (AudioManager.am != null && AudioManager.MixerGroups.TryGetValue("Ambient", out group))
			{
				src.outputAudioMixerGroup = group;
			}
			Sources[characterId] = src;
			return src;
		}

		private static float EffectsScale()
		{
			if (_underEffects == null)
			{
				_underEffects = false;
				try
				{
					AudioMixer mixer = (AudioManager.am != null) ? AudioManager.am.mixerMain : null;
					if (mixer != null)
					{
						foreach (string path in new[] { "PrefsEffects/Ambient", "Effects/Ambient" })
						{
							if (mixer.FindMatchingGroups(path).Length > 0)
							{
								_underEffects = true;
								break;
							}
						}
					}
				}
				catch
				{
				}
				Plugin.Log.LogInfo("Speech sounds: the Ambient mixer group is " + (_underEffects.Value ? "" : "not ") + "under the effects volume" + (_underEffects.Value ? "" : "; scaling by it"));
			}
			return _underEffects.Value ? 1f : Mathf.Clamp01(PlayerPrefs.GetFloat("fVolEffects", 1f));
		}

		private static void Build()
		{
			_vowel = new AudioClip[Scale.Length];
			_consonant = new AudioClip[Scale.Length];
			_radioVowel = new AudioClip[Scale.Length];
			_radioConsonant = new AudioClip[Scale.Length];
			for (int i = 0; i < Scale.Length; i++)
			{
				float hz = BaseHz * Mathf.Pow(2f, Scale[i] / 12f);
				_vowel[i] = Make("MPVoiceV" + i, hz, true, false);
				_consonant[i] = Make("MPVoiceC" + i, hz, false, false);
				_radioVowel[i] = Make("MPRadioV" + i, hz, true, true);
				_radioConsonant[i] = Make("MPRadioC" + i, hz, false, true);
			}
			_radioClick = MakeClick();
		}

		// Radio: a high-pass at 500 Hz and a third low-pass at 2.8 kHz around the voice, hiss, harder drive.
		private const float RadioLowHz = 500f;

		private const float RadioHighHz = 2800f;

		private const float RadioHiss = 0.014f;

		private const float RadioDrive = 4f;

		private static void Radioize(float[] data, int seed)
		{
			System.Random rng = new System.Random(seed);
			float hpA = Mathf.Exp(-2f * Mathf.PI * RadioLowHz / Rate);
			float lpA = 1f - Mathf.Exp(-2f * Mathf.PI * RadioHighHz / Rate);
			float hp = 0f;
			float prev = 0f;
			float lp = 0f;
			float norm = (float)Math.Tanh(RadioDrive);
			for (int i = 0; i < data.Length; i++)
			{
				float x = data[i] + (float)(rng.NextDouble() - 0.5) * 2f * RadioHiss;
				hp = hpA * (hp + x - prev);
				prev = x;
				lp += lpA * (hp - lp);
				data[i] = (float)Math.Tanh(RadioDrive * lp * 1.6f) / norm * 0.6f;
			}
		}

		// 40 ms of band-limited noise, falling off.
		private static AudioClip MakeClick()
		{
			int n = Mathf.CeilToInt(0.04f * Rate);
			float[] data = new float[n];
			System.Random rng = new System.Random(7);
			for (int i = 0; i < n; i++)
			{
				float t = (float)i / Rate;
				data[i] = (float)(rng.NextDouble() - 0.5) * 2f * 0.5f * Mathf.Exp(-t / 0.012f);
			}
			Radioize(data, 11);
			AudioClip clip = AudioClip.Create("MPRadioClick", n, 1, Rate, false);
			clip.SetData(data, 0);
			return clip;
		}

		private static AudioClip Make(string name, float hz, bool vowel, bool radio)
		{
			float dur = vowel ? 0.075f : 0.05f;
			float ringDur = dur * 1.4f;
			int n = Mathf.CeilToInt((ringDur + 0.02f) * Rate);
			float[] data = new float[n];
			float peak = vowel ? 0.3f : 0.14f;
			float ringPeak = vowel ? 0.14f : 0.08f;
			float cutoff = vowel ? 5000f : 4000f;
			double phase = 0.0;
			double ringPhase = 0.0;
			float lp1 = 0f;
			float lp2 = 0f;
			float ringLp = 0f;
			float a = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / Rate);
			float ringA = 1f - Mathf.Exp(-2f * Mathf.PI * 8000f / Rate);
			float norm = (float)Math.Tanh(Drive);
			for (int i = 0; i < n; i++)
			{
				float t = (float)i / Rate;
				float glide = Mathf.Pow(0.94f, Mathf.Min(t, dur) / dur);
				phase += 2.0 * Math.PI * hz * glide / Rate;
				ringPhase += 2.0 * Math.PI * hz * 2f * glide / Rate;
				float s = (float)Math.Sin(phase);
				float wave = vowel ? (2f / Mathf.PI) * Mathf.Asin(s) : ((s >= 0f) ? 1f : -1f);
				// Two one-pole steps: about the slope of a biquad low-pass.
				lp1 += a * (wave - lp1);
				lp2 += a * (lp1 - lp2);
				ringLp += ringA * ((float)Math.Sin(ringPhase) - ringLp);
				float x = lp2 * Envelope(t, peak, dur) + ringLp * Envelope(t, ringPeak, ringDur);
				data[i] = (float)Math.Tanh(Drive * x) / norm * 0.7f;
			}
			if (radio)
			{
				Radioize(data, name.GetHashCode());
			}
			AudioClip clip = AudioClip.Create(name, n, 1, Rate, false);
			clip.SetData(data, 0);
			return clip;
		}

		// Exponential rise over 4 ms to peak, then down to nothing at dur.
		private static float Envelope(float t, float peak, float dur)
		{
			const float floor = 0.0001f;
			const float attack = 0.004f;
			if (t >= dur)
			{
				return 0f;
			}
			if (t < attack)
			{
				return floor * Mathf.Pow(peak / floor, t / attack);
			}
			return peak * Mathf.Pow(floor / peak, (t - attack) / (dur - attack));
		}
	}
}
