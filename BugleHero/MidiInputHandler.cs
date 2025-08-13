using BugleHero;
using BugleHero.Patches;
using NAudio.Midi;
using System.Linq;
using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Diagnostics;
using System.Collections.Concurrent;

public class MidiInputHandler
{
	private MidiIn midiIn;
	private ConfigEntry<string> midiDeviceName;

	private readonly Stopwatch sw = Stopwatch.StartNew();

	// Track when each note started
	private Dictionary<int, float> activeNotes = new Dictionary<int, float>();

	private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();

	// called on MIDI thread instead of calling Unity directly
	private void EnqueueOnMainThread(Action a)
	{
		if (a != null) mainThreadQueue.Enqueue(a);
	}

	// call this from Plugin.Update() or add a Unity MonoBehaviour to process it
	public void ProcessMainThreadQueue()
	{
		while (mainThreadQueue.TryDequeue(out var action))
		{
			try { action(); }
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Exception processing MIDI action on main thread: " + ex);
			}
		}
	}

	public void Initialize(ConfigFile config)
	{
		var deviceNamesList = Enumerable.Range(0, MidiIn.NumberOfDevices)
										.Select(i => MidiIn.DeviceInfo(i).ProductName)
										.ToList();

		if (deviceNamesList.Count == 0)
		{
			Plugin.Instance.mls.LogInfo("No MIDI devices detected.");
			return;
		}

		midiDeviceName = config.Bind(
			"MIDI",
			"DeviceName",
			deviceNamesList.First(),
			new ConfigDescription(
				"Select MIDI input device",
				null,
				new AcceptableValueList<string>(deviceNamesList.ToArray())
			)
		);

		midiDeviceName.SettingChanged += (s, e) =>
		{
			OpenMidiDeviceByName(midiDeviceName.Value);
		};

		OpenMidiDeviceByName(midiDeviceName.Value);
	}

	public void OpenMidiDeviceByName(string deviceName)
	{
		int deviceIndex = -1;
		for (int i = 0; i < NAudio.Midi.MidiIn.NumberOfDevices; i++)
		{
			if (NAudio.Midi.MidiIn.DeviceInfo(i).ProductName == deviceName)
			{
				deviceIndex = i;
				break;
			}
		}

		if (deviceIndex < 0)
		{
			Plugin.Instance.mls.LogWarning($"MIDI device '{deviceName}' not found.");
			return;
		}

		StopListening();

		midiIn = new NAudio.Midi.MidiIn(deviceIndex);
		midiIn.MessageReceived += MidiIn_MessageReceived;
		midiIn.ErrorReceived += MidiIn_ErrorReceived;
		midiIn.Start();

		Plugin.Instance.mls.LogInfo($"Started listening to MIDI device #{deviceIndex}: {deviceName}");
	}

	// Keep track of sustain pedal state
	private bool sustainPedalOn = false;
	private const int MaxActiveNotes = 64;
	private const float MaxNoteAge = 5f; // seconds

	private void MidiIn_MessageReceived(object sender, MidiInMessageEventArgs e)
	{
		try
		{
			if (e.MidiEvent is ControlChangeEvent ccEvent)
			{
				if (ccEvent.Controller == MidiController.Sustain)
				{
					sustainPedalOn = ccEvent.ControllerValue >= 64;
					return; // Ignore sustain for now
				}
			}

			if (e.MidiEvent is NoteOnEvent noteOn)
			{
				int midiNote = noteOn.NoteNumber;
				if (noteOn.Velocity > 0)
				{
					// Note started - call immediately
					EnqueueOnMainThread(() =>
					{
						var bugleInstance = FindLocalBugleSFX();
						if (bugleInstance != null)
						{
							BugleMIDIPatch.OnMidiNoteOn(midiNote, bugleInstance);
						}
					});
				}
				else
				{
					// Note ended (velocity 0 = note off)
					EnqueueOnMainThread(() =>
					{
						var bugleInstance = FindLocalBugleSFX();
						if (bugleInstance != null)
						{
							BugleMIDIPatch.OnMidiNoteOff(midiNote, bugleInstance);
						}
					});
				}
			}
			else if (e.MidiEvent is NoteEvent noteEvent && noteEvent.CommandCode == MidiCommandCode.NoteOff)
			{
				// Explicit note off event
				EnqueueOnMainThread(() =>
				{
					var bugleInstance = FindLocalBugleSFX();
					if (bugleInstance != null)
					{
						BugleMIDIPatch.OnMidiNoteOff(noteEvent.NoteNumber, bugleInstance);
					}
				});
			}
		}
		catch (Exception ex)
		{
			Plugin.Instance.mls.LogWarning("Exception in MIDI handler: " + ex);
		}
	}

	private void HandleNoteOff(int midiNote)
	{
		if (activeNotes.TryGetValue(midiNote, out float startTime))
		{
			double duration = sw.Elapsed.TotalSeconds - startTime;
			activeNotes.Remove(midiNote);

			// Clamp to at least 0.25 seconds
			float clampedDuration = Mathf.Max((float)duration, 0.25f);

			// Push to Unity main thread
			EnqueueOnMainThread(() =>
			{
				var bugleInstance = FindLocalBugleSFX();
				if (bugleInstance != null)
				{
					Plugin.Instance.StartCoroutine(
						BugleMIDIPatch.OnMidiNoteReceived(midiNote, clampedDuration, bugleInstance)
					);
				}
			});
		}
	}

	private void MidiIn_ErrorReceived(object sender, MidiInMessageEventArgs e)
	{
		Plugin.Instance.mls.LogWarning("MIDI error: " + e.RawMessage);
	}

	public void StopListening()
	{
		if (midiIn != null)
		{
			midiIn.Stop();
			midiIn.Dispose();
			midiIn = null;
		}
	}

	private BugleSFX FindLocalBugleSFX()
	{
		return UnityEngine.Object.FindObjectsOfType<BugleSFX>()
			.FirstOrDefault(b => b.photonView.IsMine);
	}
}