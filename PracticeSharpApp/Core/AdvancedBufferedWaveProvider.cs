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
using System.Collections;
using NAudio.Wave;

namespace BigMansStuff.PracticeSharp.Core
{
    /// <summary>
    /// Provides a buffered store of samples
    /// Read method will return queued samples or fill buffer with zeroes
    /// based on code from trentdevers (http://naudio.codeplex.com/Thread/View.aspx?ThreadId=54133)
    /// </summary>
    public class AdvancedBufferedWaveProvider : IWaveProvider
    {
        #region Construction

        /// <summary>
        /// Constructor - Creates a new buffered WaveProvider
        /// </summary>
        /// <param name="waveFormat">WaveFormat</param>
        public AdvancedBufferedWaveProvider(WaveFormat waveFormat)
        {
            this.m_waveFormat = waveFormat;
            this.m_queue = new Queue<AudioBuffer>();
            this.MaxQueuedBuffers = 100;
        }

        #endregion

        #region Events

        public event EventHandler PlayPositionChanged;

        #endregion

        #region Public

        /// <summary>
        /// Maximum number of queued buffers
        /// </summary>
        public int MaxQueuedBuffers { get; set; }

        /// <summary>
        /// Gets the WaveFormat
        /// </summary>
        public WaveFormat WaveFormat
        {
            get { return m_waveFormat; }
        }

        /// <summary>
        /// Adds samples. Takes a copy of buffer, so that buffer can be reused if necessary
        /// </summary>
        public void AddSamples(byte[] buffer, int offset, int count, TimeSpan currentTime)
        {
            byte[] nbuffer = new byte[count];
            Buffer.BlockCopy(buffer, offset, nbuffer, 0, count);
            lock (this.m_queue)
            {
                if (this.m_queue.Count >= this.MaxQueuedBuffers)
                {
                    throw new InvalidOperationException("Too many queued buffers");
                }
                this.m_queue.Enqueue(new AudioBuffer(nbuffer, currentTime));
            }
        }

        /// <summary>
        /// IWaveProvider.Read implementation - Reads from the next queued audioBuffer into the internal NAudio buffer
        /// Will always return count bytes, since we will zero-fill the buffer if not enough available
        /// </summary>
        public int Read(byte[] buffer, int offset, int count) 
        {
            int read = 0;
            while (read < count) 
            {
                int required = count - read;
                AudioBuffer audioBuffer = null;
                lock (m_queue)
                {
                    if (m_queue.Count > 0)
                    {
                        audioBuffer = m_queue.Peek();
                    }
                }

                if (audioBuffer == null) 
                {
                    // Return a zero filled buffer
                    for (int n = 0; n < required; n++)
                        buffer[offset + n] = 0;
                    read += required;
                } 
                else // There is an audio buffer - let's play it
                {
                    int nread = audioBuffer.Buffer.Length - audioBuffer.Position;

                    // Fire PlayPositionChanged event
                    if (PlayPositionChanged != null)
                    {
                        PlayPositionChanged(this, new BufferedPlayEventArgs(audioBuffer.CurrentTime));
                    }

                    // If this buffer must be read in it's entirety
                    if (nread <= required) 
                    {
                        // Read entire buffer
                        Buffer.BlockCopy(audioBuffer.Buffer, audioBuffer.Position, buffer, offset + read, nread);
                        read += nread;

                        lock (m_queue)
                        {
                            if (m_queue.Count > 0)
                            {
                                m_queue.Dequeue();
                            }
                        }
                    }
                    else // the number of bytes that can be read is greater than that required
                    {
                        Buffer.BlockCopy(audioBuffer.Buffer, audioBuffer.Position, buffer, offset + read, required);
                        audioBuffer.Position += required;
                        read += required;
                    }
                }
            }
            return read;
        }

        /// <summary>
        /// Flushes the queue of any remaining audio buffers
        /// </summary>
        public void Flush()
        {
            lock (m_queue)
            {
                m_queue.Clear();
            }
        }

        /// <summary>
        /// Gets the current number of buffers in the queue
        /// </summary>
        public int GetQueueCount()
        {
            int queueCount = 0;
            lock (m_queue)
            {
                queueCount = m_queue.Count;
            }

            return queueCount;
        }

        #endregion
       
        #region Private Members

        private Queue<AudioBuffer> m_queue;
        private WaveFormat m_waveFormat;

        #endregion
    }

    /// <summary>
    /// Internal helper class for a stored audio buffer
    /// </summary>
    internal class AudioBuffer
    {
        /// <summary>
        /// Constructs a new AudioBuffer
        /// </summary>
        public AudioBuffer(byte[] buffer, TimeSpan currentTime)
        {
            this.Buffer = buffer;
            this.CurrentTime = currentTime;
        }

        /// <summary>
        /// Gets the Buffer
        /// </summary>
        public byte[] Buffer { get; private set; }

        /// <summary>
        /// Gets or sets the position within the buffer we have read up to so far
        /// </summary>
        public int Position { get; set; }

        /// <summary>
        /// CurrentTime of original file - used for calculating actual position within played buffer
        /// </summary>
        public TimeSpan CurrentTime { get; set; }
    }

    /// <summary>
    /// Custom Event Arguments class that is used by PlayPositionChanged event
    /// </summary>
    public class BufferedPlayEventArgs : EventArgs
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="playTime"></param>
        public BufferedPlayEventArgs(TimeSpan playTime)
        {
            this.PlayTime = playTime;
        }

        /// <summary>
        /// The current PlayTime of the buffer being played
        /// </summary>
        public TimeSpan PlayTime;
    }

}
