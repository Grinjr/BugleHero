using HarmonyLib;
using UnityEngine;
using Photon.Pun;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

namespace BugleHero.Patches
{
	[HarmonyPatch(typeof(BugleSFX))]
	internal class BugleMIDIPatch
	{
		// Track all active notes instead of just one
		private static HashSet<int> activeNotes = new HashSet<int>();
		private static int? currentPlayingNote = null; // The note currently being played (bugle can only play one at a time)

		private static int forcedClip = 0;
		private static float forcedPitch = 0f;
		private static bool forceHold = false;

		private static float loopStartTime = 0f;
		private static float loopClipLength = 0f;

		// Cleanup timer - if no activity for X seconds, force stop everything
		private static float lastActivityTime = 0f;
		private static readonly float inactivityTimeout = 5f;

		const float loopRestartDelay = 0.05f;

		// Clip frequency reference
		const float clip0BaseFreqLow = 255f;
		const float clip0BaseFreqHigh = 1025f;

		static FieldInfo currentClipField = typeof(BugleSFX).GetField("currentClip", BindingFlags.Instance | BindingFlags.NonPublic);
		static FieldInfo tField = typeof(BugleSFX).GetField("t", BindingFlags.Instance | BindingFlags.NonPublic);
		static FieldInfo currentPitchField = typeof(BugleSFX).GetField("currentPitch", BindingFlags.Instance | BindingFlags.NonPublic);

		static float MidiNoteToFreq(int midiNote) =>
			440f * Mathf.Pow(2f, (midiNote - 69) / 12f);

		static int MidiNoteToClip(int midiNote)
		{
			float freq = MidiNoteToFreq(midiNote);
			if (freq < 400f) return 0;
			if (freq < 700f) return 1;
			if (freq < 900f) return 2;
			return 3;
		}

		static float MidiNoteToPitch(int midiNote)
		{
			float freq = MidiNoteToFreq(midiNote);
			while (freq < clip0BaseFreqLow) freq *= 2f;
			while (freq > clip0BaseFreqHigh) freq /= 2f;
			return (freq - clip0BaseFreqLow) / (clip0BaseFreqHigh - clip0BaseFreqLow);
		}

		static float AdjustPitchForClip(float basePitch, int clipIndex)
		{
			float baseFreq = Mathf.Lerp(clip0BaseFreqLow, clip0BaseFreqHigh, basePitch);
			float ratio = 1f;
			if (clipIndex == 1 || clipIndex == 2) ratio = 1.0156f;
			else if (clipIndex == 3) ratio = 1.039f;

			float adjustedFreq = baseFreq * ratio;
			while (adjustedFreq > clip0BaseFreqHigh) adjustedFreq /= 2f;
			while (adjustedFreq < clip0BaseFreqLow) adjustedFreq *= 2f;

			return (adjustedFreq - clip0BaseFreqLow) / (clip0BaseFreqHigh - clip0BaseFreqLow);
		}

		public static void OnMidiNoteOn(int midiNote, BugleSFX instance)
		{
			if (instance == null) return;

			lastActivityTime = Time.time;

			// Add to active notes
			activeNotes.Add(midiNote);

			// If we're already playing a note, stop it first
			if (currentPlayingNote.HasValue)
			{
				ForceStopCurrentNote(instance);
			}

			currentPlayingNote = midiNote;

			int clipIndex = MidiNoteToClip(midiNote);
			float basePitch = MidiNoteToPitch(midiNote);
			float adjustedPitch = AdjustPitchForClip(basePitch, clipIndex);

			PlayToot(instance, clipIndex, adjustedPitch);
		}

		public static void OnMidiNoteOff(int midiNote, BugleSFX instance)
		{
			if (instance == null) return;

			lastActivityTime = Time.time;

			// Remove from active notes
			activeNotes.Remove(midiNote);

			// Only stop if this was the currently playing note
			if (currentPlayingNote == midiNote)
			{
				StopToot(instance);
				currentPlayingNote = null;

				// If there are other active notes, play the most recent one
				if (activeNotes.Count > 0)
				{
					int nextNote = activeNotes.Last(); // Play the most recently pressed note
					OnMidiNoteOn(nextNote, instance);
				}
			}
		}

		// Emergency cleanup function
		public static void ForceStopAllNotes(BugleSFX instance)
		{
			if (instance == null) return;

			Plugin.Instance.mls.LogInfo("Force stopping all stuck notes");

			activeNotes.Clear();
			currentPlayingNote = null;
			forceHold = false;

			instance.hold = false;
			tField?.SetValue(instance, false);
			instance.photonView.RPC("RPC_EndToot", RpcTarget.All);

			if (instance.buglePlayer.isPlaying)
			{
				instance.buglePlayer.Stop();
			}

			// Stop particles
			if (instance.particle1 && instance.particle1.isPlaying)
			{
				instance.particle1.Stop();
			}
			if (instance.particle2 && instance.particle2.isPlaying)
			{
				instance.particle2.Stop();
			}
		}

		private static void ForceStopCurrentNote(BugleSFX instance)
		{
			if (!currentPlayingNote.HasValue) return;

			forceHold = false;
			instance.hold = false;
			tField?.SetValue(instance, false);
			instance.photonView.RPC("RPC_EndToot", RpcTarget.All);

			if (instance.buglePlayer.isPlaying)
			{
				instance.buglePlayer.Stop();
			}
		}

		static void PlayToot(BugleSFX instance, int clipIndex, float pitch)
		{
			instance.photonView.RPC("RPC_EndToot", RpcTarget.All);
			forcedClip = clipIndex;
			forcedPitch = pitch;
			forceHold = true;

			currentClipField?.SetValue(instance, clipIndex);
			currentPitchField?.SetValue(instance, pitch);
			tField?.SetValue(instance, true);
			instance.hold = true;

			instance.buglePlayer.clip = instance.bugle[clipIndex];
			instance.buglePlayer.pitch = Mathf.Lerp(instance.pitchMin, instance.pitchMax, pitch);
			instance.buglePlayer.volume = 0f;
			instance.buglePlayer.Play();

			loopClipLength = instance.bugle[clipIndex].length;
			loopStartTime = Time.time;

			if (instance.particle1 && instance.particle2)
			{
				if (!instance.particle1.isPlaying) instance.particle1.Play();
				if (!instance.particle2.isPlaying) instance.particle2.Play();
				var emission1 = instance.particle1.emission;
				emission1.enabled = true;

				var emission2 = instance.particle2.emission;
				emission2.enabled = true;
			}

			instance.StartCoroutine(RemoteStartAfterDelay(instance, 0.05f, clipIndex, pitch));
		}

		public static void StopToot(BugleSFX instance)
		{
			forceHold = false;
			instance.hold = false;
			tField?.SetValue(instance, false);
			instance.photonView.RPC("RPC_EndToot", RpcTarget.All);
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(BugleSFX), "UpdateTooting")]
		static bool Prefix_UpdateTooting(BugleSFX __instance)
		{
			if (!__instance.photonView.IsMine) return true;

			// Check for stuck notes due to inactivity
			if (forceHold && Time.time - lastActivityTime > inactivityTimeout)
			{
				Plugin.Instance.mls.LogWarning("Detected potentially stuck note due to inactivity, forcing stop");
				ForceStopAllNotes(__instance);
				return false;
			}

			// Additional safety check - if we think we're holding but bugle player isn't playing
			if (forceHold && !__instance.buglePlayer.isPlaying && Time.time - loopStartTime > loopClipLength + 1f)
			{
				Plugin.Instance.mls.LogWarning("Detected stuck note (player not playing), forcing stop");
				ForceStopAllNotes(__instance);
				return false;
			}

			if (forceHold)
			{
				// Loop restart check
				if (Time.time - loopStartTime >= loopClipLength)
				{
					__instance.photonView.RPC("RPC_EndToot", RpcTarget.All);
					__instance.buglePlayer.Stop();

					// Restart after delay
					__instance.StartCoroutine(RestartAfterDelay(__instance, loopRestartDelay));
					loopStartTime = Time.time + loopRestartDelay;
				}
				return false;
			}
			return true;
		}

		static System.Collections.IEnumerator RestartAfterDelay(BugleSFX instance, float delay)
		{
			yield return new WaitForSeconds(delay);

			// Additional safety check before restarting
			if (forceHold && currentPlayingNote.HasValue)
			{
				instance.photonView.RPC("RPC_StartToot", RpcTarget.Others, forcedClip, forcedPitch);
				PlayToot(instance, forcedClip, forcedPitch);
			}
		}

		static System.Collections.IEnumerator RemoteStartAfterDelay(BugleSFX instance, float delay, int clip, float pitch)
		{
			yield return new WaitForSeconds(delay);
			instance.photonView.RPC("RPC_StartToot", RpcTarget.Others, clip, pitch);
		}

		// Public method you can call from elsewhere to manually fix stuck notes
		public static void EmergencyStopAll()
		{
			var bugleInstance = UnityEngine.Object.FindObjectsOfType<BugleSFX>()
				.FirstOrDefault(b => b.photonView.IsMine);

			if (bugleInstance != null)
			{
				ForceStopAllNotes(bugleInstance);
			}
		}
	}
}