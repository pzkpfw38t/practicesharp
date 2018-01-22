﻿#region © Copyright 2010 Yuval Naveh, Practice Sharp. LGPL.
/* Practice Sharp
 
    © Copyright 2010, Yuval Naveh.
     All rights reserved.
 
    This file is part of Practice Sharp.

    Practice Sharp is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    Practice Sharp is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Lesser Public License for more details.

    You should have received a copy of the GNU Lesser Public License
    along with Practice Sharp.  If not, see <http://www.gnu.org/licenses/>.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel;
using System.Threading;
using System.IO;
using BigMansStuff.NAudio.Ogg;
using BigMansStuff.PracticeSharp.SoundTouch;
using NLog;
using NAudio.Wave;
using NAudio.WindowsMediaFormat;
using NAudio.Flac;

namespace BigMansStuff.PracticeSharp.Core
{
    /// <summary>
    /// Core Logic (Back-End) for PracticeSharp application
    /// Acts as a mediator between the User Interface, NAudio and SoundSharp layers
    /// </summary>
    public sealed class PracticeSharpLogic : IDisposable
    {
        #region Logger
        private static Logger m_logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize the PracticeSharp core logic layer
        /// </summary>
        public void Initialize()
        {
            ChangeStatus(Statuses.Initializing);
            m_startMarker = TimeSpan.Zero;
            m_endMarker = TimeSpan.Zero;
            m_cue = TimeSpan.Zero;
            m_suppressVocals = false;
            m_inputChannelsMode = InputChannelsModes.Both;
            m_swapLeftRightSpeakers = false;

            InitializeSoundTouchSharp();
        }

        /// <summary>
        /// Loads the given file name and start playing it
        /// </summary>
        /// <param name="filename"></param>
        public void LoadFile(string filename)
        {
            // Stop a previous file that is currently being played
            if (m_audioProcessingThread != null)
            {
                Stop();
            }

            m_filename = filename;
            m_status = Statuses.Loading;

            StartAudioThread(filename);

            // Wait for thread for finish initialization
            lock (InitializedLock)
            {
                if (!Monitor.Wait(InitializedLock, 5000))
                {
                    m_logger.Error("Initialization lock timeout");
                }
            }

        }

        /// <summary>
        /// Starts/Resumes play back of the current file
        /// </summary>
        public void Play()
        {
            m_logger.Debug("Play - " + m_status.ToString());
            // Not playing now - Start the audio processing thread
            if (m_status == Statuses.Ready)
            {
                lock (FirstPlayLock)
                {
                    m_logger.Debug("Monitor: Play(), Pulse FirstPlayLock");
                    Monitor.PulseAll(FirstPlayLock);
                }
            }
            else if (m_status == Statuses.Pausing)
            {
                if (m_newPlayTimeRequested)
                {
                    // Flush existing buffers
                    m_soundTouchSharp.Clear();
                    m_waveChannel.Flush();
                    m_inputProvider.Flush();
                }

                m_waveOutDevice.Play();
                ChangeStatus(Statuses.Playing);
            }
        }

        /// <summary>
        /// Stops the play back
        /// </summary>
        /// <remarks>
        /// This is a hard stop that cannot be resumed.
        ///  </remarks>
        public void Stop()
        {
            // Already stopped? Nothing to do
            if (m_status == Statuses.Stopped)
                return;

            // Stop the audio processing thread
            m_stopWorker = true;

            // Release lock, if current thread has not started playback
            lock (FirstPlayLock)
            {
                m_logger.Debug("Stop: Pulse FirstPlayLock");
                Monitor.PulseAll(FirstPlayLock);
            }
         
            // Wait for audio thread to stop (Up to 2000 msec), if not just give up
            int counter = 100;
            while (m_audioProcessingThread != null && counter > 0)
            {
                Thread.Sleep(20);
                counter--;
            }
        }

        /// <summary>
        /// Pauses the play back
        /// </summary>
        public void Pause()
        {
            // Playback status changed to -> Pausing
            m_waveOutDevice.Pause();
            Thread.Sleep(20); // Allow the audio to pause and settle
            ChangeStatus(Statuses.Pausing);
        }

        /// <summary>
        /// Reset the current play to the begining (start marker or begining of time)
        /// </summary>
        public void ResetCurrentPlayTime()
        {
            // Reset current play time so it starts from the beginning
            CurrentPlayTime = StartMarker;

            // Signal the UI about the cue
            CueWaitPulsed(this, EventArgs.Empty);
        }

        /// <summary>
        /// Utility function that identifies wether the file is an audio file or not
        /// </summary>
        /// <param name="filename"></param>
        /// <returns></returns>
        public static bool IsAudioFile(string filename)
        {
            filename = filename.ToLower();
            bool result = filename.EndsWith(MP3Extension) ||
                          filename.EndsWith(M4AExtension) ||
                          filename.EndsWith(WAVExtension) ||
                          filename.EndsWith(OGGVExtension) ||
                          filename.EndsWith(FLACExtension) ||
                          filename.EndsWith(WMAExtension) ||
                          filename.EndsWith(AIFFExtension);

            return result;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Getter of the file play duration - the length of the file playing time
        /// </summary>
        public TimeSpan FilePlayDuration
        {
            get
            {
                return m_filePlayDuration;
            }
        }

        /// <summary>
        /// Property for controlling the play back Tempo (Speed)
        /// Domain values - A non-negative floating number (normally 0.1 - 3.0)
        /// 1.0 = Regular speed
        /// greater than 1.0 = Faster (e.g. 2.0 runs two times faster)
        /// less than 1.0 = Slower (e.g. 0.5 runs two times slower)
        /// </summary>
        /// <remarks>
        /// To be used from the controlling component (i.e. the GUI)
        /// </remarks>
        public float Tempo
        {
            get { lock (TempoLock) return m_tempo; }
            set { lock (TempoLock) { m_tempo = value; m_tempoChanged = true; } }
        }

        /// <summary>
        /// Property for controlling the play back Pitch
        /// Domain values - Semi-Tones (-12.0 to +12.0, -12 is one octave down/+12 is one octave up)
        /// 0.0 = Regular Pitch
        /// </summary>
        /// <remarks>
        /// To be used from the controlling component (i.e. the GUI)
        /// </remarks>
        public float Pitch
        {
            get { lock (PropertiesLock) return m_pitch; }
            set { lock (PropertiesLock) { m_pitch = value; m_pitchChanged = true; } }
        }

        /// <summary>
        /// Play back Volume in percent - 0%..100%
        /// </summary>
        public float Volume
        {
            get
            {
                lock (PropertiesLock) { return m_volume; }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_volume = value;
                    m_volumeChanged = true;
                }
            }
        }

        public float EqualizerLoBand
        {
            get
            {
                lock (PropertiesLock) { return m_eqLo; }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_eqLo = value;
                    m_eqParamsChanged = true;
                }
            }
        }

        public float EqualizerMedBand
        {
            get
            {
                lock (PropertiesLock) { return m_eqMed; }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_eqMed = value;
                    m_eqParamsChanged = true;
                }
            }
        }

        public float EqualizerHiBand
        {
            get
            {
                lock (PropertiesLock) { return m_eqHi; }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_eqHi = value;
                    m_eqParamsChanged = true;
                }
            }
        }

        
        public InputChannelsModes InputChannelsMode
        {
            get
            {
                lock (PropertiesLock) { return m_inputChannelsMode; }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_inputChannelsMode = value;
                }
            }
        }

        /// <summary>
        /// Sets the Swap Speakers (Left<->Right) mode on or off
        /// </summary>
        public bool SwapLeftRightSpeakers
        {
            get
            {
                lock (PropertiesLock) { return m_swapLeftRightSpeakers; }
            }
            set
            {
                lock (PropertiesLock) {
                    m_swapLeftRightSpeakers = value;
                }
            }
        }


        public TimeStretchProfile TimeStretchProfile
        {
            get
            {
                lock (PropertiesLock) { return m_timeStretchProfile; }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_timeStretchProfile = value;
                    m_timeStretchProfileChanged = true;
                }
            }
        }

        /// <summary>
        /// Current status
        /// </summary>
        public Statuses Status { get { return m_status; } }

        /// <summary>
        /// The current real play time - taking into account the tempo value
        /// </summary>
        public TimeSpan CurrentPlayTime
        {
            get
            {
                if (m_inputProvider == null)
                    return TimeSpan.Zero;

                lock (CurrentPlayTimeLock)
                {
                    return m_currentPlayTime;
                }
            }
            set
            {
                TimeSpan newPlayTime = value;
                lock (NewPlayTimeLock)
                {
                    // Limit minimum and maximum
                    if (newPlayTime < TimeSpan.Zero)
                        newPlayTime = TimeSpan.Zero;
                    else if (newPlayTime > m_filePlayDuration)
                        newPlayTime = m_filePlayDuration;
                    m_newPlayTimeRequested = true;
                    m_newPlayTime = newPlayTime;
                }

                // For normal non-playing statuses update the current playing time immediately
                if (m_status == Statuses.Pausing || m_status == Statuses.Ready || m_status == Statuses.Stopped) 
                {
                    lock (CurrentPlayTimeLock)
                    {
                        m_currentPlayTime = newPlayTime;
                    }
                }
            }
        }

        /// <summary>
        /// Boolean flag for playing the selected region (or whole file) in a loop
        ///     True - play on loop
        ///     False - play once and stop
        /// </summary>
        public bool Loop
        {
            get
            {
                lock (LoopLock)
                {
                    return m_loop;
                }
            }
            set
            {
                lock (LoopLock)
                {
                    m_loop = value;
                }
            }
        }

        public TimeSpan StartMarker
        {
            get
            {
                if (m_inputProvider == null)
                    return TimeSpan.Zero;

                lock (PropertiesLock)
                {
                    return m_startMarker;
                }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_startMarker = value;
                }
            }
        }

        public TimeSpan EndMarker
        {
            get
            {
                if (m_inputProvider == null)
                    return TimeSpan.Zero;

                lock (PropertiesLock)
                {
                    return m_endMarker;
                }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_endMarker = value;
                }
            }
        }

        public TimeSpan Cue
        {
            get
            {
                if (m_inputProvider == null)
                    return TimeSpan.Zero;

                lock (PropertiesLock)
                {
                    return m_cue;
                }
            }
            set
            {
                lock (PropertiesLock)
                {
                    m_cue = value;
                }
            }
        }

        /// <summary>
        /// Supresses the vocals part of the song
        /// </summary>
        public bool SuppressVocals
        {
            get
            {
                lock (PropertiesLock) { return m_suppressVocals; }
            }
            set
            {
                lock (PropertiesLock) { m_suppressVocals = value; }
            }
        }

        #endregion

        #region Events

        public event EventHandler PlayTimeChanged;
        public delegate void StatusChangedEventHandler(object sender, Statuses newStatus);
        public event StatusChangedEventHandler StatusChanged;
        public event EventHandler CueWaitPulsed;

        #endregion

        #region Enums

        public enum Statuses { None, Initializing, Ready, Loading, Playing, Stopped, Pausing, Terminating, Terminated, Error };

        #endregion

        #region Private Methods

        /// <summary>
        /// Audio processing thread procedure
        /// </summary>
        private void AudioProcessingWorker_DoWork()
        {
            m_stopWorker = false;

            try
            {
                // Initialize audio playback
                try
                {
                    InitializeFileAudio();

                    InitializeEqualizerEffect();

                    bool newPlayTimeRequested;
                    lock (NewPlayTimeLock)
                    {
                         newPlayTimeRequested = m_newPlayTimeRequested;
                    }

                    lock (CurrentPlayTimeLock)
                    {
                        // Special case handling for re-playing after last playing stopped in non-loop mode: 
                        //   Reset current play time to begining of file in case the previous play has reached the end of file
                        if (!newPlayTimeRequested && m_currentPlayTime >= m_waveChannel.TotalTime)
                        {                       
                            m_currentPlayTime = TimeSpan.Zero;
                        }
                    }

                    // Playback status changed to -> Initialized
                    ChangeStatus(Statuses.Ready);
                }
                finally
                {
                    // Pulse the initialized lock to release the client (UI) that is waiting for initialization to finish
                    lock (InitializedLock)
                    {
                        m_logger.Debug("Monitor: Pulse InitializedLock");
                        Monitor.PulseAll(InitializedLock);
                    }
                }

                // Wait for first Play to pulse and free lock
                lock (FirstPlayLock)
                {
                    m_logger.Debug("Monitor: Wait for FirstPlayLock");
                    Monitor.Wait(FirstPlayLock);
                }
                m_logger.Debug("Monitor: FirstPlayLock got pulsed");

                // Safety guard - if thread never really started playing but PracticeSharpLogic was terminated ore started terminating
                if (m_waveOutDevice == null || m_status == Statuses.Terminating || m_status == Statuses.Terminated)
                {
                    m_logger.Warn("SHOULD NOT GET HERE - Terminated before play started");
                    return;
                }

                try
                {
                    if (m_stopWorker)
                    {
                        m_logger.Debug("Stopped before playing ever started");
                        return;
                    }

                    m_logger.Debug("waveOutDevice.Play()");
                    // Command NAudio to start playing
                    m_waveOutDevice.Play();
                    // Playback status changed to -> Playing
                    ChangeStatus(Statuses.Playing);
               
                    // ==============================================
                    // ====  Perform the actual audio processing ====
                    ProcessAudio();
                    // ==============================================
                }
                finally
                {
                    // Dispose of NAudio in context of thread (for WMF it must be disposed in the same thread)
                    TerminateNAudio();

                    m_audioProcessingThread = null;
                }
            }
            catch (Exception ex)
            {
                m_logger.Error(ex, "Exception in audioProcessingWorker_DoWork, ");
                ChangeStatus(Statuses.Error);
            }
        }

        /// <summary>
        /// The heart of Practice# audio processing:
        /// 1. Reads chunks of uncompressed samples from the input file
        /// 2. Processes the samples using SoundTouch 
        /// 3. Receive process samples from SoundTouch
        /// 4. Play the processed samples with NAudio
        /// 
        /// Also handles logic required for dynamically changing values on-the-fly of: Volume, Loop, Cue, Current Play Position.
        /// </summary>
        private void ProcessAudio()
        {
            m_logger.Debug("ProcessAudio() started");

            #region Setup
            WaveFormat format = m_waveChannel.WaveFormat;
            int bufferSecondLength = format.SampleRate * format.Channels;
            byte[] inputBuffer = new byte[BufferSamples * sizeof(float)];
            byte[] soundTouchOutBuffer = new byte[BufferSamples * sizeof(float)];

            ByteAndFloatsConverter convertInputBuffer = new ByteAndFloatsConverter { Bytes = inputBuffer };
            ByteAndFloatsConverter convertOutputBuffer = new ByteAndFloatsConverter { Bytes = soundTouchOutBuffer };
            uint outBufferSizeFloats = (uint)convertOutputBuffer.Bytes.Length / (uint)(sizeof(float) * format.Channels);

            int bytesRead;
            int floatsRead;
            uint samplesProcessed = 0;
            int bufferIndex = 0;
            TimeSpan actualEndMarker = TimeSpan.Zero;
            bool loop;

            #endregion

            bool isWaitForCue = (Cue.TotalSeconds > 0);

            while (!m_stopWorker && m_waveChannel.Position < m_waveChannel.Length)
            {
                #region Handle Volume Change request
                if (m_volumeChanged) // Double checked locking
                {
                    lock (PropertiesLock)
                    {
                        if (m_volumeChanged)
                        {
                            m_waveChannel.Volume = m_volume;
                            m_volumeChanged = false;
                        }
                    }
                }
                #endregion

                #region Handle New play position request

                TimeSpan newPlayTime = TimeSpan.MinValue;
                if (m_newPlayTimeRequested) // Double checked locking
                {
                    lock (NewPlayTimeLock)
                    {
                        if (m_newPlayTimeRequested)
                        {
                            m_logger.Debug("newPlayTimeRequested: " + m_newPlayTime);
                            if (m_newPlayTime == m_startMarker)
                            {
                                isWaitForCue = true;
                            }

                            newPlayTime = m_newPlayTime;
                            m_newPlayTimeRequested = false;
                        }
                    }
                }

                #endregion

                #region Wait for Cue

                if (isWaitForCue)
                {
                    isWaitForCue = false;
                    WaitForCue();
                }

                #endregion

                #region [Only when new play position requested] Change current play position

                if (newPlayTime != TimeSpan.MinValue)
                {
                    // Perform the change of play position outside of the lock() block, to avoid dead locks
                    m_waveOutDevice.Pause();
                    m_waveChannel.CurrentTime = newPlayTime;
                    m_soundTouchSharp.Clear();
                    m_waveChannel.Flush();
                    m_inputProvider.Flush();
                    m_waveOutDevice.Play();
                    continue;
                }

                #endregion

                #region Read samples from file

                // *** Read one chunk from input file ***
                bytesRead = m_waveChannel.Read(convertInputBuffer.Bytes, 0, convertInputBuffer.Bytes.Length);
                // **************************************

                floatsRead = bytesRead / ((sizeof(float)) * format.Channels);

                #endregion

                #region Apply DSP Effects (Equalizer, Vocal Removal, etc.)

                ApplyDSPEffects(convertInputBuffer.Floats, floatsRead);

                #endregion

                #region Apply Time Stretch Profile

                // Double checked locking
                if (m_timeStretchProfileChanged)
                {
                    lock (PropertiesLock)
                    {
                        if (m_timeStretchProfileChanged)
                        {
                            ApplySoundTouchTimeStretchProfile();

                            m_timeStretchProfileChanged = false;
                        }
                    }
                }

                #endregion

                #region Handle End Marker
                actualEndMarker = this.EndMarker;
                loop = this.Loop;

                if (!loop || actualEndMarker == TimeSpan.Zero)
                    actualEndMarker = m_waveChannel.TotalTime;

                if (m_waveChannel.CurrentTime > actualEndMarker)
                {
                    #region Flush left over samples

                    // ** End marker reached **
                    // Now the input buffer is processed, 'flush' some last samples that are
                    // hiding in the SoundTouch's internal processing pipeline.
                    m_soundTouchSharp.Clear();
                    m_inputProvider.Flush();
                    m_waveChannel.Flush();

                    if (!m_stopWorker)
                    {
                        while (!m_stopWorker && samplesProcessed != 0)
                        {
                            samplesProcessed = m_soundTouchSharp.ReceiveSamples(convertOutputBuffer.Floats, outBufferSizeFloats);

                            if (samplesProcessed > 0)
                            {
                                TimeSpan currentBufferTime = m_waveChannel.CurrentTime;
                                m_inputProvider.AddSamples(convertOutputBuffer.Bytes, 0, (int)samplesProcessed * sizeof(float) * format.Channels, currentBufferTime);
                            }
                        }
                    }

                    #endregion

                    #region Perform Loop
                    loop = this.Loop;
                    if (loop)
                    {
                        m_soundTouchSharp.Clear();
                        m_waveChannel.Flush();
                        m_waveChannel.CurrentTime = this.StartMarker;
                        isWaitForCue = (Cue.TotalSeconds > 0);
                        continue;
                    }
                    else
                    {
                        // Exit playback gracefully
                        break;
                    }

                    #endregion
                }
                #endregion

                #region Put samples in SoundTouch

                SetSoundSharpValues();

                // ***                    Put samples in SoundTouch                   ***
                m_soundTouchSharp.PutSamples(convertInputBuffer.Floats, (uint)floatsRead);
                // **********************************************************************

                #endregion

                #region Receive & Play Samples
                // Receive samples from SoundTouch
                do
                {
                    // ***                Receive samples back from SoundTouch            ***
                    // *** This is where Time Stretching and Pitch Changing are actually done *********
                    samplesProcessed = m_soundTouchSharp.ReceiveSamples(convertOutputBuffer.Floats, outBufferSizeFloats);
                    // **********************************************************************

                    if (samplesProcessed > 0)
                    {
                        TimeSpan currentBufferTime = m_waveChannel.CurrentTime;

                        // ** Play samples that came out of SoundTouch by adding them to AdvancedBufferedWaveProvider - the buffered player 
                        m_inputProvider.AddSamples(convertOutputBuffer.Bytes, 0, (int)samplesProcessed * sizeof(float) * format.Channels, currentBufferTime);
                        // **********************************************************************

                        // Wait for queue to free up - only then add continue reading from the file
                        // >> Note: when paused, loop runs infinitely
                        while (!m_stopWorker && m_inputProvider.GetQueueCount() > BusyQueuedBuffersThreshold)
                        {
                            Thread.Sleep(10);
                        }
                        bufferIndex++;
                    }
                } while (!m_stopWorker && samplesProcessed != 0);
                #endregion
            } // while

            #region Stop PlayBack

            m_logger.Debug("ProcessAudio() finished - stop playback");
            m_waveOutDevice.Stop();
            // Stop listening to PlayPositionChanged events
            m_inputProvider.PlayPositionChanged -= new EventHandler(InputProvider_PlayPositionChanged);

            // Fix to current play time not finishing up at end marker (Wave channel uses positions)
            if (!m_stopWorker && CurrentPlayTime < actualEndMarker)
            {
                lock (CurrentPlayTimeLock)
                {
                    m_currentPlayTime = actualEndMarker;
                }
            }

            // Clear left over buffers
            m_soundTouchSharp.Clear();

            // Playback status changed to -> Stopped
            ChangeStatus(Statuses.Stopped);
            #endregion
        }


        /// <summary>
        /// Applies the DSP Effects in the effects chain
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="count"></param>
        private void ApplyDSPEffects(float[] buffer, int count)
        {
            int samples = count * 2;

            bool suppressVocals;
            // Apply Equalizer parameters (if they were changed)
            lock (PropertiesLock)
            {
                suppressVocals = m_suppressVocals;

                if (m_eqParamsChanged)
                {
                    lock (PropertiesLock)
                    {
                        if (m_eqParamsChanged) // Double check locking
                        {
                            m_eqEffect.LoGainFactor.Value = m_eqEffect.LoGainFactor.Maximum * m_eqLo;
                            //m_eqEffect.LoDriveFactor.Value = (m_eqLo + 1.0f) / 2 * 100.0f;
                            m_eqEffect.MedGainFactor.Value = m_eqEffect.MedGainFactor.Maximum * m_eqMed;
                            // m_eqEffect.MedDriveFactor.Value = (m_eqMed + 1.0f) / 2 * 100.0f;
                            m_eqEffect.HiGainFactor.Value = m_eqEffect.HiGainFactor.Maximum * m_eqHi;
                            //m_eqEffect.HiDriveFactor.Value = (m_eqHi + 1.0f) / 2 * 100.0f;

                            m_eqEffect.OnFactorChanges();

                            m_eqParamsChanged = false;
                        }
                    }
                }
            }

            // Run each sample in the buffer through the equalizer effect
            for (int sample = 0; sample < samples; sample += 2)
            {
                // Get the samples, per audio channel
                float sampleLeft = buffer[sample];
                float sampleRight = buffer[sample + 1];

                // Apply the equalizer effect to the samples
                m_eqEffect.Sample(ref sampleLeft, ref sampleRight);

                if (suppressVocals)
                {
                    // Suppression of vocals assumes vocals are recorded in the 'Center'
                    // The suppression results in two mono channels (instead of the original Stereo)
                    float supressedVocalChannel = (sampleLeft - sampleRight) * 0.7f;
                    sampleLeft = supressedVocalChannel;
                    sampleRight = supressedVocalChannel;
                }

                // Put the modified samples back into the buffer, with input channel selection mode
                float finalRightSample; 
                float finalLeftSample;
                if (m_inputChannelsMode == InputChannelsModes.DualMono) 
                {
                    finalRightSample = ( sampleRight + sampleLeft ) * 0.7f; 
                    finalLeftSample = finalRightSample;
                }
                else 
                {
                    finalRightSample = m_inputChannelsMode == InputChannelsModes.Right ? sampleRight : sampleLeft; 
                    finalLeftSample = m_inputChannelsMode == InputChannelsModes.Left ? sampleLeft : sampleRight;
                }

                if (m_swapLeftRightSpeakers)
                {
                    var temp = finalRightSample;
                    finalRightSample = finalLeftSample;
                    finalLeftSample = temp;
                }

                buffer[sample] = finalRightSample;
                buffer[sample + 1] = finalLeftSample;
            }
        }

        /// <summary>
        /// Initialize the file play back audio infrastructure
        /// </summary>
        private void InitializeFileAudio()
        {
            InitializeNAudioLibrary();

            CreateSoundTouchInputProvider(m_filename);

            try
            {
                m_waveOutDevice.Init(m_inputProvider);
            }
            catch (Exception initException)
            {
                m_logger.Error(initException, "Exception in InitializeFileAudio - m_waveOutDevice.Init");

                throw;
            }

            m_filePlayDuration = m_waveChannel.TotalTime;
        }

        /// <summary>
        /// Waits for the loop cue - basically waits a few seconds before the loop starts again, this allows the musician to rest a bit and prepare
        /// </summary>
        private void WaitForCue()
        {
            TimeSpan cue = this.Cue;
            if (cue.TotalSeconds > 0)
            {
                // Wait Cue - 1 seconds in a slow pulse (one per second) busy-loop
                int pulseCount = 0;
                while (!m_stopWorker && pulseCount < cue.TotalSeconds - 1)
                {
                    RaiseEventCueWaitPulsed();

                    Thread.Sleep(1000);
                    pulseCount++;
                }

                // Wait 1 seconds in a fast pulse (4 per second) busy-loop
                pulseCount = 0;
                while (!m_stopWorker && pulseCount < 4)
                {
                    RaiseEventCueWaitPulsed();

                    Thread.Sleep(250);
                    pulseCount++;
                }
            }
        }

        /// <summary>
        /// Raises the CueWaitPulsed event in an asynchronous way
        /// </summary>
        private void RaiseEventCueWaitPulsed()
        {
            if (CueWaitPulsed != null)
            {
                // explicitly invoke each subscribed event handler *asynchronously*
                foreach (EventHandler subscriber in CueWaitPulsed.GetInvocationList())
                {
                    // Event is unidirectional - No call back (i.e. EndInvoke) needed
                    subscriber.BeginInvoke(this, new EventArgs(), null, subscriber);
                }
            }
        }

        /// <summary>
        /// Changes the status and raises the StatusChanged event
        /// </summary>
        /// <param name="newStatus"></param>
        private void ChangeStatus(Statuses newStatus)
        {
            m_status = newStatus;

            if (m_logger.IsDebugEnabled) m_logger.Debug("PracticeSharpLogic - Status changed: " + m_status);
            // Raise StatusChanged Event
            if (StatusChanged != null)
            {
                // explicitly invoke each subscribed event handler *asynchronously*
                foreach (StatusChangedEventHandler subscriber in StatusChanged.GetInvocationList())
                {
                    // Event is unidirectional - No call back (i.e. EndInvoke) needed
                    subscriber.BeginInvoke(this, newStatus, null, subscriber);
                }
            }
        }

        /// <summary>
        /// Creates input provider needed for reading an audio file into the SoundTouch library
        /// </summary>
        /// <param name="filename"></param>
        private void CreateSoundTouchInputProvider(string filename)
        {
            CreateInputWaveChannel(filename);

            WaveFormat format = m_waveChannel.WaveFormat;
            m_inputProvider = new AdvancedBufferedWaveProvider(format);
            m_inputProvider.PlayPositionChanged += new EventHandler(InputProvider_PlayPositionChanged);
            m_inputProvider.MaxQueuedBuffers = 100;

            m_soundTouchSharp.SetSampleRate(format.SampleRate);
            m_soundTouchSharp.SetChannels(format.Channels);

            m_soundTouchSharp.SetTempoChange(0);
            m_soundTouchSharp.SetPitchSemiTones(0);
            m_soundTouchSharp.SetRateChange(0);

            m_soundTouchSharp.SetTempo(m_tempo);

            // Apply default SoundTouch settings
            m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_USE_QUICKSEEK, 0);

            ApplySoundTouchTimeStretchProfile();
        }

        private void ApplySoundTouchTimeStretchProfile()
        {
            // "Disable" sound touch AA and revert to Automatic settings at regular tempo (to remove side effects)
            if (Math.Abs(m_tempo - 1) < 0.001)
            {
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_USE_AA_FILTER, 0);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_AA_FILTER_LENGTH, 0);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_OVERLAP_MS, 0);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_SEQUENCE_MS, 0);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_SEEKWINDOW_MS, 0);
            }
            else
            {
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_USE_AA_FILTER, m_timeStretchProfile.UseAAFilter ? 1 : 0);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_AA_FILTER_LENGTH, m_timeStretchProfile.AAFilterLength);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_OVERLAP_MS, m_timeStretchProfile.Overlap);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_SEQUENCE_MS, m_timeStretchProfile.Sequence);
                m_soundTouchSharp.SetSetting(SoundTouchSharp.SoundTouchSettings.SETTING_SEEKWINDOW_MS, m_timeStretchProfile.SeekWindow);
            }
        }

        /// <summary>
        /// Sets the SoundSharp values - tempo & pitch
        /// </summary>
        /// <param name="tempo"></param>
        /// <param name="pitch"></param>
        private void SetSoundSharpValues()
        {
            if (m_tempoChanged)
            {
                lock (PropertiesLock)
                {
                    if (m_tempoChanged) // Double Check Locking
                    {
                        float tempo = this.Tempo;
                        // Assign updated tempo
                        m_soundTouchSharp.SetTempo(tempo);
                        m_tempoChanged = false;

                        ApplySoundTouchTimeStretchProfile();
                    }
                }
            }

            if (m_pitchChanged)
            {
                lock (PropertiesLock)
                {
                    if (m_pitchChanged) // Double Check Locking
                    {
                        float pitch = this.Pitch;
                        // Assign updated pitch
                        // m_soundTouchSharp.SetPitchOctaves(pitch);
                        m_soundTouchSharp.SetPitchSemiTones(pitch);
                        m_pitchChanged = false;
                    }
                }
            }
        }

        /// <summary>
        /// NAudio Event handler - Fired every time the play position has changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void InputProvider_PlayPositionChanged(object sender, EventArgs e)
        {
            lock (CurrentPlayTimeLock)
            {
                m_currentPlayTime = (e as BufferedPlayEventArgs).PlayTime;
            }

            RaiseEventPlayTimeChanged();
        }

        /// <summary>
        /// Raises the PlayTimeChanged event in an asynchronous way
        /// </summary>
        private void RaiseEventPlayTimeChanged()
        {
            if (PlayTimeChanged != null)
            {
                // explicitly invoke each subscribed event handler *asynchronously*
                foreach (EventHandler subscriber in PlayTimeChanged.GetInvocationList())
                {
                    // Event is unidirectional - No call back (i.e. EndInvoke) needed
                    subscriber.BeginInvoke(this, new EventArgs(), null, subscriber);
                }
            }
        }

        /// <summary>
        /// Creates an input WaveChannel (Audio file reader for MP3/WAV/OGG/FLAC/WMA/AIFF/Other formats in the future)
        /// </summary>
        /// <param name="filename"></param>
        private void CreateInputWaveChannel(string filename)
        {
            string fileExt = Path.GetExtension(filename.ToLower());
            if (fileExt == MP3Extension)
            {
                m_waveReader = new Mp3FileReader(filename);
                m_blockAlignedStream = new BlockAlignReductionStream(m_waveReader);
                // Wave channel - reads from file and returns raw wave blocks
                m_waveChannel = new WaveChannel32(m_blockAlignedStream);
            }
            else if (fileExt == M4AExtension)
            {
                m_waveReader = new MediaFoundationReader(filename);
                m_blockAlignedStream = new BlockAlignReductionStream(m_waveReader);
                // Wave channel - reads from file and returns raw wave blocks
                m_waveChannel = new WaveChannel32(m_blockAlignedStream);
            }
            else if (fileExt == WAVExtension)
            {
                m_waveReader = new WaveFileReader(filename);
                if (m_waveReader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                {
                    m_waveReader = WaveFormatConversionStream.CreatePcmStream(m_waveReader);
                    m_waveReader = new BlockAlignReductionStream(m_waveReader);
                }
                if (m_waveReader.WaveFormat.BitsPerSample != 16)
                {
                    var format = new WaveFormat(m_waveReader.WaveFormat.SampleRate,
                       16, m_waveReader.WaveFormat.Channels);
                    m_waveReader = new WaveFormatConversionStream(format, m_waveReader);
                }

                m_waveChannel = new WaveChannel32(m_waveReader);
            }
            else if (fileExt == OGGVExtension)
            {
                m_waveReader = new OggVorbisFileReader(filename);
                if (m_waveReader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                {
                    m_waveReader = WaveFormatConversionStream.CreatePcmStream(m_waveReader);
                    m_waveReader = new BlockAlignReductionStream(m_waveReader);
                }
                if (m_waveReader.WaveFormat.BitsPerSample != 16)
                {
                    var format = new WaveFormat(m_waveReader.WaveFormat.SampleRate,
                       16, m_waveReader.WaveFormat.Channels);
                    m_waveReader = new WaveFormatConversionStream(format, m_waveReader);
                }

                m_waveChannel = new WaveChannel32(m_waveReader);
            }
            else if (fileExt == FLACExtension)
            {
                m_waveReader = new FlacReader(filename);
                if (m_waveReader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                {
                    m_waveReader = WaveFormatConversionStream.CreatePcmStream(m_waveReader);
                    m_waveReader = new BlockAlignReductionStream(m_waveReader);
                }
                if (m_waveReader.WaveFormat.BitsPerSample != 16)
                {
                    var format = new WaveFormat(m_waveReader.WaveFormat.SampleRate,
                       16, m_waveReader.WaveFormat.Channels);
                    m_waveReader = new WaveFormatConversionStream(format, m_waveReader);
                }

                m_waveChannel = new WaveChannel32(m_waveReader);
            }
            else if (fileExt == WMAExtension)
            {
                m_waveReader = new WMAFileReader(filename);
                if (m_waveReader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                {
                    m_waveReader = WaveFormatConversionStream.CreatePcmStream(m_waveReader);
                    m_waveReader = new BlockAlignReductionStream(m_waveReader);
                }
                if (m_waveReader.WaveFormat.BitsPerSample != 16)
                {
                    var format = new WaveFormat(m_waveReader.WaveFormat.SampleRate,
                       16, m_waveReader.WaveFormat.Channels);
                    m_waveReader = new WaveFormatConversionStream(format, m_waveReader);
                }

                m_waveChannel = new WaveChannel32(m_waveReader);
            }
            else if (fileExt == AIFFExtension)
            {
                m_waveReader = new AiffFileReader(filename);
                m_waveChannel = new WaveChannel32(m_waveReader);
            }
            else
            {
                throw new ApplicationException("Cannot create Input WaveChannel - Unknown file type: " + fileExt);
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the NAudio framework
        /// </summary>
        private void InitializeNAudioLibrary()
        {
            try
            {
                m_latency = Properties.Settings.Default.Latency;

                m_logger.Info("OS Info: " + Environment.OSVersion.ToString());

                // string soundOutput = "WasapiOut";
                string soundOutput = "WaveOut";

                // Set the wave output device based on the configuration setting
                switch (soundOutput)
                {
                    case "WasapiOut":
                        m_waveOutDevice = new WasapiOut(global::NAudio.CoreAudioApi.AudioClientShareMode.Shared, m_latency);
                        break;

                    case "DirectSound":
                        m_waveOutDevice = new DirectSoundOut(m_latency);
                        break;

                    default:
                    case "WaveOut":
                        m_waveOutDevice = new WaveOut();
                        break;
                }

                m_waveOutDevice.PlaybackStopped += WaveOutDevice_PlaybackStopped;
                m_logger.Info("Wave Output Device that is actually being used: {0}", m_waveOutDevice.GetType().ToString());
            }
            catch (Exception driverCreateException)
            {
                m_logger.Error(driverCreateException, "NAudio Driver Creation Failed");
                throw;
            }
        }

        void WaveOutDevice_PlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (e == null || e.Exception == null)
                return;

            m_logger.Error(e.Exception, "waveOutDevice_PlaybackStopped");
        }

        /// <summary>
        /// Initialize the sound touch library (using the SoundTouchSharp wrapper)
        /// </summary>
        private void InitializeSoundTouchSharp()
        {
            m_soundTouchSharp = new SoundTouchSharp();
            m_soundTouchSharp.CreateInstance();
            m_logger.Info("SoundTouch Initialized - Version: " + m_soundTouchSharp.SoundTouchVersionId + ", " + m_soundTouchSharp.SoundTouchVersionString);
        }

        /// <summary>
        /// Initialize the Equalizer DSP Effect
        /// </summary>
        private void InitializeEqualizerEffect()
        {
            // Initialize Equalizer
            m_eqEffect = new EqualizerEffect
            {
                SampleRate = m_waveChannel.WaveFormat.SampleRate
            };

            m_eqEffect.LoDriveFactor.Value = 75;
            m_eqEffect.LoGainFactor.Value = 0;
            m_eqEffect.MedDriveFactor.Value = 40;
            m_eqEffect.MedGainFactor.Value = 0;
            m_eqEffect.HiDriveFactor.Value = 30;
            m_eqEffect.HiGainFactor.Value = 0;
            m_eqEffect.Init();
            m_eqEffect.OnFactorChanges();
        }

        /// <summary>
        /// Creates and starts the audio thread for the given audio filename
        /// </summary>
        /// <param name="filename"></param>
        private void StartAudioThread(string filename)
        {
            // Create the Audio Processing Worker (Thread)
            m_audioProcessingThread = new Thread(new ThreadStart(AudioProcessingWorker_DoWork))
            {
                Name = "AudioProcessingThread-" + filename,
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            // Important: MTA is needed for WMFSDK to function properly (for WMA support)
            // All WMA (COM) related actions MUST be done within the Thread's MTA otherwise there is a COM exception
            m_audioProcessingThread.SetApartmentState(ApartmentState.MTA);

            // Allow initialization to start >>Inside<< the thread, the thread will stop and wait for a pulse
            m_audioProcessingThread.Start();
        }

        #endregion

        #region Termination

        /// <summary>
        /// Disposes of the current allocated resources
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_waveReader"),
        System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_blockAlignedStream"),
        System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_waveChannel"),
        System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_waveOutDevice")]
        public void Dispose()
        {
            if (m_status != Statuses.Terminated)
            {
                Terminate();
            }

            GC.SuppressFinalize(this);
        }


        /// <summary>
        /// Terminates all current play back and resources
        /// </summary>
        public void Terminate()
        {
            ChangeStatus(Statuses.Terminating);

            Stop();

            // Dispose of SoundTouchSharp
            TerminateSoundTouchSharp();

            ChangeStatus(Statuses.Terminated);
        }

        /// <summary>
        /// Terminates the NAudio library resources and connection
        /// </summary>
        private void TerminateNAudio()
        {
            if (m_inputProvider != null)
            {
                if (m_inputProvider is IDisposable)
                {
                    (m_inputProvider as IDisposable).Dispose();
                }
                m_inputProvider = null;
            }

            if (m_waveChannel != null)
            {
                m_waveChannel.Dispose();
                m_waveChannel = null;
            }

            if (m_blockAlignedStream != null)
            {
                m_blockAlignedStream.Dispose();
                m_blockAlignedStream = null;
            }

            if (m_waveReader != null)
            {
                m_waveReader.Dispose();
                m_waveReader = null;
            }

            if (m_waveOutDevice != null)
            {
                m_waveOutDevice.Dispose();
                m_waveOutDevice = null;
            }

            m_logger.Debug("NAudio terminated");
        }

        /// <summary>
        /// Terminates the SoundTouch resources and connection
        /// </summary>
        private void TerminateSoundTouchSharp()
        {
            if (m_soundTouchSharp != null)
            {
                m_soundTouchSharp.Clear();
                m_soundTouchSharp.Dispose();
                m_soundTouchSharp = null;
                m_logger.Debug("SoundTouch terminated");
            }
        }

        #endregion

        #region Private Members

        private Statuses m_status = Statuses.None;
        private string m_filename;
        private int m_latency = 125; // msec

        private SoundTouchSharp m_soundTouchSharp;
        private IWavePlayer m_waveOutDevice;
        private AdvancedBufferedWaveProvider m_inputProvider;

        private WaveStream m_blockAlignedStream = null;
        private WaveStream m_waveReader = null;
        private WaveChannel32 m_waveChannel = null;

        private float m_tempo = 1f;
        private float m_pitch = 0f;
        private bool m_loop;
        private float m_volume;
        private volatile bool m_volumeChanged = false;
        private float m_eqLo;
        private float m_eqMed;
        private float m_eqHi;
        private InputChannelsModes m_inputChannelsMode;
        private bool m_swapLeftRightSpeakers;
        private volatile bool m_eqParamsChanged;
        private EqualizerEffect m_eqEffect;

        private TimeStretchProfile m_timeStretchProfile;
        private volatile bool m_timeStretchProfileChanged;

        private Thread m_audioProcessingThread;
        private volatile bool m_stopWorker = false;

        private TimeSpan m_filePlayDuration;

        private TimeSpan m_startMarker;
        private TimeSpan m_endMarker;
        private TimeSpan m_cue;
        private bool m_suppressVocals;

        private TimeSpan m_currentPlayTime;
        private TimeSpan m_newPlayTime;
        private volatile bool m_newPlayTimeRequested; // Double checked locking - http://www.cs.colorado.edu/~kena/classes/5828/s12/presentation-materials/goldbergdrew.pdf
        private volatile bool m_tempoChanged = true;
        private volatile bool m_pitchChanged = true;

        private List<DSPEffect> m_dspEffects = new List<DSPEffect>();

        // Thread Locks
        private readonly object LoopLock = new object();
        private readonly object CurrentPlayTimeLock = new object();
        private readonly object NewPlayTimeLock = new object();
        private readonly object InitializedLock = new object();
        private readonly object FirstPlayLock = new object();
        private readonly object TempoLock = new object();
        private readonly object PropertiesLock = new object();

        #endregion

        #region Constants

        const string MP3Extension = ".mp3";
        const string M4AExtension = ".m4a";
        const string WAVExtension = ".wav";
        const string OGGVExtension = ".ogg";
        const string FLACExtension = ".flac";
        const string WMAExtension = ".wma";
        const string AIFFExtension = ".aiff";

        const int BusyQueuedBuffersThreshold = 3;

        const int BufferSamples = 5 * 2048; // floats, not bytes

        #endregion
    }
}
