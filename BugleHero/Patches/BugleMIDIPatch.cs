using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine.InputSystem;
using UnityEngine;
using Photon.Pun;
using System.Reflection;
using System.Collections;

namespace BugleHero.Patches
{
	[HarmonyPatch(typeof(BugleSFX))]
	internal class BugleMIDIPatch
	{
		// Track currently playing notes
		private static HashSet<int> currentlyPlayingNotes = new HashSet<int>();

		[HarmonyPatch("Update")]
		[HarmonyPostfix]
		static void MIDIOutput(BugleSFX __instance)
		{
			if (!__instance.photonView.IsMine)
				return;

		}

		// Base frequencies per clip (using clip 0's low and high as the pitch 0 and 1 range)
		const float clip0BaseFreqLow = 255f;    // pitch = 0
		const float clip0BaseFreqHigh = 1025f;  // pitch = 1

		// Transpose MIDI note into playable range (e.g., 48 to 96)
		static int TransposeMidiNoteIntoRange(int midiNote, int minNote = 48, int maxNote = 96)
		{
			int note = midiNote;
			while (note < minNote) note += 12;
			while (note > maxNote) note -= 12;
			return note;
		}

		// Convert MIDI note number to frequency in Hz (A4=440Hz standard)
		static float MidiNoteToFreq(int midiNote)
		{
			return 440f * Mathf.Pow(2f, (midiNote - 69) / 12f);
		}

		// Pick clip index by frequency bands
		static int MidiNoteToClip(int midiNote)
		{
			float freq = MidiNoteToFreq(midiNote);
			if (freq < 400f) return 0;      // Low range, clip 0
			else if (freq < 700f) return 1; // Mid-low, clip 1
			else if (freq < 900f) return 2; // Mid-high, clip 2
			else return 3;                  // High, clip 3
		}

		// Map MIDI note to pitch param 0–1 based on clip 0 base frequencies
		// MODIFIED: Now transposes frequencies outside the range instead of clamping
		static float MidiNoteToPitch(int midiNote)
		{
			float freq = MidiNoteToFreq(midiNote);

			// Transpose frequency into the valid range using octave shifts
			float rangeSize = clip0BaseFreqHigh - clip0BaseFreqLow;

			// If frequency is too low, shift it up by octaves (multiply by 2)
			while (freq < clip0BaseFreqLow)
			{
				freq *= 2f;
			}

			// If frequency is too high, shift it down by octaves (divide by 2)
			while (freq > clip0BaseFreqHigh)
			{
				freq /= 2f;
			}

			// Now convert the transposed frequency to pitch 0-1
			return (freq - clip0BaseFreqLow) / (clip0BaseFreqHigh - clip0BaseFreqLow);
		}

		// Adjust pitch for clip based on frequency ratio (multiplicative)
		static float AdjustPitchForClip(float basePitch, int clipIndex)
		{
			// Convert basePitch back to frequency in Hz
			float baseFreq = Mathf.Lerp(clip0BaseFreqLow, clip0BaseFreqHigh, basePitch);

			// Frequency ratios per clip relative to clip 0 (based on your observations)
			float ratio = 1f;
			if (clipIndex == 1 || clipIndex == 2)
				ratio = 1.0156f; // ~1.5% higher frequency
			else if (clipIndex == 3)
				ratio = 1.039f;  // ~3.9% higher frequency

			// Apply ratio to get new frequency
			float adjustedFreq = baseFreq * ratio;

			// Make sure the adjusted frequency stays in range
			// If it goes out of range after adjustment, transpose it back
			while (adjustedFreq > clip0BaseFreqHigh)
			{
				adjustedFreq /= 2f;
			}
			while (adjustedFreq < clip0BaseFreqLow)
			{
				adjustedFreq *= 2f;
			}

			// Convert adjusted frequency back to pitch 0–1 range
			float adjustedPitch = (adjustedFreq - clip0BaseFreqLow) / (clip0BaseFreqHigh - clip0BaseFreqLow);
			return adjustedPitch;
		}

		// Handle note on events
		public static void OnMidiNoteOn(int midiNote, BugleSFX bugleInstance)
		{
			try
			{
				if (bugleInstance == null) return;

				// Add this note to currently playing
				currentlyPlayingNotes.Add(midiNote);

				// Always switch to the new note immediately
				// Calculate audio parameters for this note
				int clipIndex = MidiNoteToClip(midiNote);
				float basePitch = MidiNoteToPitch(midiNote);
				float adjustedPitch = AdjustPitchForClip(basePitch, clipIndex);

				// Switch to this note (seamless transition, no stop/start)
				SwitchToNote(bugleInstance, clipIndex, adjustedPitch);

				Plugin.Instance.mls.LogInfo($"Note ON: {midiNote}, Active notes: {currentlyPlayingNotes.Count}");
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Exception in OnMidiNoteOn: " + ex);
			}
		}

		// Handle note off events
		public static void OnMidiNoteOff(int midiNote, BugleSFX bugleInstance)
		{
			try
			{
				if (bugleInstance == null) return;

				// Remove this note from currently playing
				currentlyPlayingNotes.Remove(midiNote);

				Plugin.Instance.mls.LogInfo($"Note OFF: {midiNote}, Active notes: {currentlyPlayingNotes.Count}");

				// If no notes are playing, stop the bugle
				if (currentlyPlayingNotes.Count == 0)
				{
					StopToot(bugleInstance);
				}
				// If other notes are still playing, switch to the highest remaining note
				else
				{
					// Switch to the highest currently active note
					int highestNote = currentlyPlayingNotes.Max();
					int clipIndex = MidiNoteToClip(highestNote);
					float basePitch = MidiNoteToPitch(highestNote);
					float adjustedPitch = AdjustPitchForClip(basePitch, clipIndex);

					// Switch to the highest active note (seamless transition)
					SwitchToNote(bugleInstance, clipIndex, adjustedPitch);
				}
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Exception in OnMidiNoteOff: " + ex);
			}
		}

		// Track start times of each forced hold by note
		static Dictionary<int, float> holdStartTimes = new Dictionary<int, float>();

		static float GetClipLength(BugleSFX instance, int clipIndex)
		{
			if (instance == null || instance.bugle == null || clipIndex < 0 || clipIndex >= instance.bugle.Length) return 0f;
			return instance.bugle[clipIndex].length;
		}

		static int? queuedClip = null;
		static float? queuedPitch = null;

		static void SwitchToNote(BugleSFX instance, int clipIndex, float pitch)
		{
			if (stopCooldownActive)
			{
				// Queue the note to play after cooldown instead of ignoring
				queuedClip = clipIndex;
				queuedPitch = pitch;
				Plugin.Instance.mls.LogInfo("Note queued due to cooldown.");
				return;
			}
			if (instance == null) return;
			if (instance.bugle == null || instance.bugle.Length == 0) return;
			if (instance.buglePlayer == null) return;
			if (!instance.gameObject.activeInHierarchy) return;
			if (clipIndex < 0 || clipIndex >= instance.bugle.Length) return;

			// Clear any queued note, since we're about to play this one now
			queuedClip = null;
			queuedPitch = null;

			forcedClip = clipIndex;
			forcedPitch = pitch;
			forceHold = true;

			holdStartTimes[clipIndex] = Time.time; // record start time

			// Set private fields via reflection
			currentClipField?.SetValue(instance, clipIndex);
			currentPitchField?.SetValue(instance, pitch);
			tField?.SetValue(instance, true);

			instance.hold = true;

			try
			{
				// If the clip is different, switch the audio clip
				if (instance.buglePlayer.clip != instance.bugle[clipIndex])
				{
					instance.buglePlayer.clip = instance.bugle[clipIndex];
					// Only restart playback if we changed clips
					if (!instance.buglePlayer.isPlaying)
					{
						instance.buglePlayer.Play();
					}
				}

				// Always update pitch and volume (seamless)
				instance.buglePlayer.pitch = Mathf.Lerp(instance.pitchMin, instance.pitchMax, pitch);
				instance.buglePlayer.volume = 0f; // Let the game handle volume fade-in
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Failed to switch bugle audio: " + ex);
				return;
			}

			try
			{
				// Handle particles
				if (instance.particle1 && instance.particle2)
				{
					if (!instance.particle1.isPlaying) instance.particle1.Play();
					if (!instance.particle2.isPlaying) instance.particle2.Play();

					var emission1 = instance.particle1.emission;
					var emission2 = instance.particle2.emission;
					emission1.enabled = true;
					emission2.enabled = true;
				}
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Particle play failed: " + ex);
			}

			try
			{
				// Send RPC to other players
				if (instance.photonView != null)
				{
					instance.photonView.RPC("RPC_StartToot", Photon.Pun.RpcTarget.Others, clipIndex, pitch);
				}
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("RPC_StartToot failed: " + ex);
			}
		}

		// Legacy method for backwards compatibility (no longer needed for real-time MIDI)
		public static IEnumerator OnMidiNoteReceived(int midiNote, float durationSeconds, BugleSFX bugleInstance)
		{
			// For backwards compatibility, simulate note on/off
			OnMidiNoteOn(midiNote, bugleInstance);
			yield return new WaitForSeconds(durationSeconds);
			OnMidiNoteOff(midiNote, bugleInstance);
		}

		static IEnumerator PlayNoteWithDuration(BugleSFX instance, int clipIndex, float pitch, float duration)
		{
			duration = Mathf.Max((float)duration, 0.25f);
			PlayToot(instance, clipIndex, pitch);
			yield return new WaitForSeconds(duration);
			StopToot(instance);
		}

		static bool forceHold = false;
		static int forcedClip = 0;
		static float forcedPitch = 0f;

		static bool stopCooldownActive = false;
		static float stopCooldownEndTime = 0f;
		const float stopCooldownDuration = 0.05f;

		static IEnumerator LoopToot(BugleSFX instance)
		{
			if (instance == null) yield break;

			// Stop current toot, clearing force hold etc
			StopToot(instance);

			// Wait a short time to let things properly reset (50ms)
			yield return new WaitForSeconds(0.05f);

			// Restart the note (same clip & pitch)
			SwitchToNote(instance, forcedClip, forcedPitch);

			// Reset start time for this clip so looping continues properly
			holdStartTimes[forcedClip] = Time.time;
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(BugleSFX), "UpdateTooting")]
		static bool Prefix_UpdateTooting(BugleSFX __instance)
		{
			if (!__instance.photonView.IsMine)
				return true;

			if (stopCooldownActive && Time.time >= stopCooldownEndTime)
			{
				stopCooldownActive = false;

				// If a note is queued, play it now
				if (queuedClip.HasValue && queuedPitch.HasValue)
				{
					SwitchToNote(__instance, queuedClip.Value, queuedPitch.Value);
					queuedClip = null;
					queuedPitch = null;
				}
			}

			if (forceHold)
			{
				float clipLength = GetClipLength(__instance, forcedClip);
				if (clipLength > 0 && holdStartTimes.TryGetValue(forcedClip, out float startTime))
				{
					if (Time.time - startTime >= clipLength)
					{
						StopToot(__instance);
						forceHold = false;

						stopCooldownActive = true;
						stopCooldownEndTime = Time.time + stopCooldownDuration;

						return false;
					}
				}

				if (!__instance.hold)
				{
					__instance.photonView.RPC("RPC_StartToot", RpcTarget.All, forcedClip, forcedPitch);
					__instance.hold = true;
				}

				return false;
			}
			else
			{
				if (__instance.hold)
				{
					__instance.photonView.RPC("RPC_EndToot", RpcTarget.All);
					__instance.hold = false;
				}
				return true;
			}
		}

		static FieldInfo currentClipField = typeof(BugleSFX).GetField("currentClip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		static FieldInfo tField = typeof(BugleSFX).GetField("t", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		static FieldInfo currentPitchField = typeof(BugleSFX).GetField("currentPitch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		static void PlayToot(BugleSFX instance, int clipIndex, float pitch)
		{
			if (instance == null) return;
			if (instance.bugle == null || instance.bugle.Length == 0) return;
			if (instance.buglePlayer == null) return;
			if (!instance.gameObject.activeInHierarchy) return;
			if (clipIndex < 0 || clipIndex >= instance.bugle.Length) return;

			forcedClip = clipIndex;
			forcedPitch = pitch;
			forceHold = true;

			// Set private fields via reflection
			currentClipField?.SetValue(instance, clipIndex);
			currentPitchField?.SetValue(instance, pitch);
			tField?.SetValue(instance, true);

			instance.hold = true;

			try
			{
				instance.buglePlayer.clip = instance.bugle[clipIndex];
				instance.buglePlayer.pitch = Mathf.Lerp(instance.pitchMin, instance.pitchMax, pitch);
				instance.buglePlayer.volume = 0f;
				instance.buglePlayer.Play();
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Failed to play bugle audio: " + ex);
				return;
			}

			try
			{
				if (instance.particle1 && instance.particle2)
				{
					if (!instance.particle1.isPlaying) instance.particle1.Play();
					if (!instance.particle2.isPlaying) instance.particle2.Play();

					var emission1 = instance.particle1.emission;
					var emission2 = instance.particle2.emission;
					emission1.enabled = true;
					emission2.enabled = true;
				}
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Particle play failed: " + ex);
			}

			try
			{
				if (instance.photonView != null)
				{
					instance.photonView.RPC("RPC_StartToot", Photon.Pun.RpcTarget.Others, clipIndex, pitch);
				}
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("RPC_StartToot failed: " + ex);
			}
		}

		public static void StopToot(BugleSFX instance)
		{
			if (instance == null) return;

			forceHold = false;

			try
			{
				if (instance.photonView != null)
				{
					instance.photonView.RPC("RPC_EndToot", RpcTarget.All);
				}
			}
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("RPC_EndToot failed: " + ex);
			}

			instance.hold = false;
			tField?.SetValue(instance, false); // Clear the private t flag
		}
	}
}