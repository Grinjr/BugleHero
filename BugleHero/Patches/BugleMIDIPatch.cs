using HarmonyLib;
using UnityEngine;
using Photon.Pun;
using System;
using System.Reflection;

namespace BugleHero.Patches
{
	[HarmonyPatch(typeof(BugleSFX))]
	internal class BugleMIDIPatch
	{
		private static int? currentNote = null;
		private static int forcedClip = 0;
		private static float forcedPitch = 0f;
		private static bool forceHold = false;

		private static float loopStartTime = 0f;
		private static float loopClipLength = 0f;

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

			currentNote = midiNote;

			int clipIndex = MidiNoteToClip(midiNote);
			float basePitch = MidiNoteToPitch(midiNote);
			float adjustedPitch = AdjustPitchForClip(basePitch, clipIndex);

			PlayToot(instance, clipIndex, adjustedPitch);
		}

		public static void OnMidiNoteOff(int midiNote, BugleSFX instance)
		{
			if (instance == null) return;
			if (currentNote != midiNote) return;

			StopToot(instance);
			currentNote = null;
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
			//instance.photonView.RPC("RPC_StartToot", RpcTarget.Others, clipIndex, pitch);
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
			instance.photonView.RPC("RPC_StartToot", RpcTarget.Others, forcedClip, forcedPitch);
			PlayToot(instance, forcedClip, forcedPitch);
		}

		static System.Collections.IEnumerator RemoteStartAfterDelay(BugleSFX instance, float delay, int clip, float pitch)
		{
			yield return new WaitForSeconds(delay);
			instance.photonView.RPC("RPC_StartToot", RpcTarget.Others, clip, pitch);
		}
	}
}
