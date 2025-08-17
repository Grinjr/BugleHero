using BugleHero;
using BugleHero.Patches;
using NAudio.Midi;
using System.Linq;
using BepInEx.Configuration;
using System.Collections.Concurrent;
using UnityEngine;
using System;
using System.Collections.Generic;

public class MidiInputHandler
{
	private MidiIn midiIn;
	private ConfigEntry<string> midiDeviceName;


	private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();

	// Track expected note states to detect stuck notes
	private readonly HashSet<int> expectedActiveNotes = new HashSet<int>();
	private float lastMidiActivity = 0f;

	private void EnqueueOnMainThread(Action a)
	{
		if (a != null) mainThreadQueue.Enqueue(a);
	}

	public void ProcessMainThreadQueue()
	{
		// Process MIDI actions
		while (mainThreadQueue.TryDequeue(out var action))
		{
			try { action(); }
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Exception processing MIDI action: " + ex);
			}
		}

		// Safety check for stuck notes due to missed MIDI messages
		CheckForStuckNotes();
	}

	private void CheckForStuckNotes()
	{
		// If we haven't received MIDI input for a while but think notes are active, something might be stuck
		if (expectedActiveNotes.Count > 0 && Time.time - lastMidiActivity > 10f)
		{
			Plugin.Instance.mls.LogWarning($"No MIDI activity for 10 seconds but {expectedActiveNotes.Count} notes expected active. Clearing expected notes.");
			expectedActiveNotes.Clear();

			// Also force stop everything as a safety measure
			BugleMIDIPatch.EmergencyStopAll();
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
			new ConfigDescription("Select MIDI input device", null,
				new AcceptableValueList<string>(deviceNamesList.ToArray()))
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
		for (int i = 0; i < MidiIn.NumberOfDevices; i++)
		{
			if (MidiIn.DeviceInfo(i).ProductName == deviceName)
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

		midiIn = new MidiIn(deviceIndex);
		midiIn.MessageReceived += MidiIn_MessageReceived;
		midiIn.ErrorReceived += MidiIn_ErrorReceived;
		midiIn.Start();

		Plugin.Instance.mls.LogInfo($"Started listening to MIDI device #{deviceIndex}: {deviceName}");
	}

	private void MidiIn_MessageReceived(object sender, MidiInMessageEventArgs e)
	{
		try
		{
			lastMidiActivity = Time.time;

			if (e.MidiEvent is NoteOnEvent noteOn)
			{
				int midiNote = noteOn.NoteNumber;

				if (noteOn.Velocity > 0)
				{
					expectedActiveNotes.Add(midiNote);
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
					// Some MIDI devices send Note On with velocity 0 instead of Note Off
					expectedActiveNotes.Remove(midiNote);
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
				expectedActiveNotes.Remove(noteEvent.NoteNumber);
				EnqueueOnMainThread(() =>
				{
					var bugleInstance = FindLocalBugleSFX();
					if (bugleInstance != null)
					{
						BugleMIDIPatch.OnMidiNoteOff(noteEvent.NoteNumber, bugleInstance);
					}
				});
			}
			else if (e.MidiEvent is ControlChangeEvent ccEvent)
			{
				// Handle common MIDI CC messages that might affect note state
				// CC 120 = All Sound Off, CC 123 = All Notes Off
				if (ccEvent.Controller == (MidiController)120 ||
					ccEvent.Controller == (MidiController)123)
				{
					Plugin.Instance.mls.LogInfo($"Received All Notes/Sound Off MIDI message (CC {(int)ccEvent.Controller})");
					expectedActiveNotes.Clear();
					EnqueueOnMainThread(() =>
					{
						BugleMIDIPatch.EmergencyStopAll();
					});
				}
				else if (ccEvent.Controller == MidiController.Sustain)
				{
					// Handle sustain pedal - you might want to modify this behavior
					Plugin.Instance.mls.LogInfo($"Sustain pedal: {ccEvent.ControllerValue}");
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Instance.mls.LogWarning("Exception in MIDI handler: " + ex);
		}
	}

	private void MidiIn_ErrorReceived(object sender, MidiInMessageEventArgs e)
	{
		Plugin.Instance.mls.LogWarning("MIDI error: " + e.RawMessage);

		// On MIDI errors, clear our expected state as it might be unreliable
		expectedActiveNotes.Clear();
	}

	public void StopListening()
	{
		// Clean up when stopping
		expectedActiveNotes.Clear();
		BugleMIDIPatch.EmergencyStopAll();

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

	// Manual method to force stop all notes - you can call this from console commands or debug UI
	public void EmergencyStopAllNotes()
	{
		Plugin.Instance.mls.LogInfo("Manual emergency stop triggered");
		expectedActiveNotes.Clear();
		BugleMIDIPatch.EmergencyStopAll();
	}
}