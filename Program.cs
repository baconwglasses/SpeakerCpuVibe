// Requirements: pulseaudio-utils, Linux only.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SpeakerCpuVibe
{
    internal class Program
    {
        // ---------------- Configuration ----------------

        // How many worker threads to spin up. One per logical core gives the
        // clearest per-core "dancing" in a CPU monitor (e.g. `htop`).
        private static readonly int WorkerCount = Environment.ProcessorCount; // e.g. 12

        // Length of one duty-cycle "frame" for the busy/sleep pattern.
        // Smaller = more responsive to the music, but a bit more scheduling overhead.
        private static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(60);

        // Loudness (0..1 RMS) below this is treated as silence -> ~idle CPU.
        private const float SilenceFloor = 0.01f;

        // Floor/ceiling for the load percentage we're willing to apply per core.
        private const float MinLoad = 0f;
        private const float MaxLoad = 1f;

        // Smoothing.
        private const float AttackFactor = 1f;  // how fast load rises to match a loud moment
        private const float ReleaseFactor = 1f; // how fast load falls after it gets quiet

        // Frequency weighting: split the signal into a low band (bass/kick) and a
        // high band (everything above it), then weight them differently so bass
        // hits drive the CPU graph harder than hi-hats/cymbals/vocals do.
        private const float BassCutoffHz = 150f; // below this = "bass"
        private const float MultiplierFactor = 1.5f;
        private const float BassWeight = 1.15f * MultiplierFactor;  // >1 lets a strong kick alone push load high
        private const float TrebleWeight = 0.25f * MultiplierFactor; // highs contribute, but softly

        // Capture format we ask `parec` for. float32 little-endian keeps the
        // sample math identical to the original WASAPI-float path.
        private const int SampleRate = 44100;
        private const int Channels = 2;
        private const int BytesPerSample = 4; // float32

        // -------------------------------------------------

        private static volatile float _targetLoad = MinLoad; // 0..1, shared across worker threads
        private static volatile bool _running = true;
        private static Process? _parecProcess;

        // Raw (pre-weighting) peaks from the most recent chunk, exposed purely
        // for the status line so you can tell "no signal reaching us" apart
        // from "signal arrived late". Not used in the load calculation itself.
        private static volatile float _debugBassPeak = 0f;
        private static volatile float _debugTreblePeak = 0f;

        private static void Main()
        {
            Console.WriteLine($"SpeakerCpuVibe starting with {WorkerCount} worker threads.");

            string? monitorSource = FindDefaultMonitorSource();
            if (monitorSource == null)
            {
                Console.WriteLine("[error] Could not determine the default audio monitor source.");
                Console.WriteLine("        Make sure PulseAudio or PipeWire (with pulse compatibility)");
                Console.WriteLine("        is running and `pactl`/`parec` are installed (pulseaudio-utils).");
                return;
            }

            Console.WriteLine($"Capturing from monitor source: {monitorSource}");
            Console.WriteLine("Play some music. Watch your CPU monitor (e.g. htop). Ctrl+C to stop.\n");

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _running = false;
                try { _parecProcess?.Kill(); } catch { /* already gone */ }
            };

            // Start the load-generating worker threads.
            for (int i = 0; i < WorkerCount; i++)
            {
                var t = new Thread(WorkerLoop) { IsBackground = true, Name = $"VibeWorker-{i}" };
                t.Start();
            }

            // Start listening to the default output via its monitor source.
            var captureThread = new Thread(() => CaptureLoop(monitorSource))
            {
                IsBackground = true,
                Name = "CaptureLoop"
            };
            captureThread.Start();

            // Status printer. Shows raw bass/treble peaks alongside the target
            // load: if those stay near 0 while music is playing, the problem is
            // upstream (wrong monitor source, volume too low, muted, etc.) --
            // not the load generation itself.
            while (_running)
            {
                Console.Write($"\r[{DateTime.Now:HH:mm:ss}] target load: {_targetLoad * 100,5:0.0}%   " +
                               $"raw bass: {_debugBassPeak,5:0.000}  raw treble: {_debugTreblePeak,5:0.000}   ");
                Thread.Sleep(200);
            }

            Console.WriteLine("\nStopped.");
        }

        /// Asks PulseAudio/PipeWire (via `pactl`) for the default sink, then
        /// returns its ".monitor" source, which is what `parec` needs to
        /// capture "whatever is currently playing" (loopback-style).
        private static string? FindDefaultMonitorSource()
        {
            try
            {
                string defaultSink = RunAndReadStdout("pactl", "get-default-sink").Trim();
                if (!string.IsNullOrEmpty(defaultSink))
                    return $"{defaultSink}.monitor";
            }
            catch (Win32Exception)
            {
                Console.WriteLine("[error] `pactl` not found on PATH. Install pulseaudio-utils.");
                return null;
            }
            catch
            {
                // fall through to the short-list fallback below
            }

            // Fallback: pick the first monitor source from `pactl list short sources`.
            try
            {
                string sources = RunAndReadStdout("pactl", "list short sources");
                foreach (string line in sources.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 2 && fields[1].EndsWith(".monitor", StringComparison.Ordinal))
                        return fields[1];
                }
            }
            catch
            {
                // ignored, caller reports failure
            }

            return null;
        }

        private static string RunAndReadStdout(string fileName, string arguments)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        /// Launches `parec` against the given monitor source and continuously
        /// reads raw float32 PCM from it, updating _targetLoad from the
        /// loudness of each chunk. Runs until _running is false.
        private static void CaptureLoop(string monitorSource)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "parec",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add($"--device={monitorSource}");
            psi.ArgumentList.Add("--format=float32le");
            psi.ArgumentList.Add($"--rate={SampleRate}");
            psi.ArgumentList.Add($"--channels={Channels}");
            psi.ArgumentList.Add("--raw");
            // Monitor sources default to a fairly large server-side buffer
            // (often 1-2 seconds), which is where most of the "delayed"
            // feeling comes from. Ask for a short target latency instead.
            psi.ArgumentList.Add("--latency-msec=20");
            psi.ArgumentList.Add("--process-time-msec=10");

            Process parec;
            try
            {
                parec = Process.Start(psi)!;
            }
            catch (Win32Exception)
            {
                Console.WriteLine("[error] `parec` not found on PATH. Install pulseaudio-utils.");
                _running = false;
                return;
            }

            _parecProcess = parec;

            var bassFilter = new OnePoleLowPass(BassCutoffHz, SampleRate);
            float smoothedLoudness = 0f;

            Stream stdout = parec.StandardOutput.BaseStream;
            const int chunkFrames = 256; // ~5.8ms of audio per read at 44.1kHz, keeps latency low
            byte[] buffer = new byte[chunkFrames * Channels * BytesPerSample];

            try
            {
                while (_running)
                {
                    int bytesRead = ReadFully(stdout, buffer);
                    if (bytesRead <= 0)
                        break; // parec exited/stream closed

                    (float bassPeak, float treblePeak) = ComputeBandPeaks(buffer, bytesRead, bassFilter);
                    _debugBassPeak = bassPeak;
                    _debugTreblePeak = treblePeak;

                    float weighted = BassWeight * bassPeak + TrebleWeight * treblePeak;
                    float peak = Math.Clamp(weighted, 0f, 1f);

                    // Attack/release smoothing: rise fast on loud transients, fall slower after.
                    float factor = peak > smoothedLoudness ? AttackFactor : ReleaseFactor;
                    smoothedLoudness += (peak - smoothedLoudness) * factor;

                    float loudness = smoothedLoudness < SilenceFloor ? 0f : smoothedLoudness;
                    _targetLoad = Math.Clamp(MinLoad + loudness * (MaxLoad - MinLoad), MinLoad, MaxLoad);
                }
            }
            catch (IOException)
            {
                // stream closed on shutdown; nothing to do
            }
            finally
            {
                try { if (!parec.HasExited) parec.Kill(); } catch { /* ignore */ }
            }
        }

        /// Stream.Read isn't guaranteed to fill the buffer in one call; loop
        /// until it's full, the stream ends, or we're shutting down.
        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length && _running)
            {
                int n = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (n <= 0)
                    break; // EOF
                totalRead += n;
            }
            return totalRead;
        }

        /// Each worker busy-spins for (targetLoad * FramePeriod) then sleeps for the
        /// remainder of the frame, repeatedly. Over many short frames this makes the
        /// OS-reported CPU usage track _targetLoad closely without fully pegging a core.
        private static void WorkerLoop()
        {
            var sw = Stopwatch.StartNew();
            long frameTicks = FramePeriod.Ticks;

            while (_running)
            {
                float load = _targetLoad; // snapshot, shared value updates continuously
                long busyTicks = (long)(frameTicks * load);
                long frameStart = sw.ElapsedTicks;

                // Busy-spin (deliberately wasted work, not sleeping) to generate real load.
                while (sw.ElapsedTicks - frameStart < busyTicks)
                {
                    // spin
                }

                long remainingTicks = frameTicks - (sw.ElapsedTicks - frameStart);
                if (remainingTicks > 0)
                {
                    int sleepMs = (int)(remainingTicks / TimeSpan.TicksPerMillisecond);
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                }
            }
        }

        /// Splits a raw interleaved float32 capture buffer into a bass-band peak
        /// and a treble-band peak. Bass = output of a low-pass filter. Treble =
        /// what the low-pass removed (sample minus its low-passed value), i.e. a
        /// cheap complementary high-pass. Channels are treated independently but
        /// folded into one shared filter state, which is fine for peak-detection
        /// purposes here.
        private static (float bassPeak, float treblePeak) ComputeBandPeaks(
            byte[] buffer, int bytesRecorded, OnePoleLowPass bassFilter)
        {
            float bassPeak = 0f;
            float treblePeak = 0f;

            int sampleCount = bytesRecorded / BytesPerSample;
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(buffer, i * BytesPerSample);

                float low = bassFilter.Process(sample);
                float high = sample - low;

                float absLow = Math.Abs(low);
                float absHigh = Math.Abs(high);
                if (absLow > bassPeak) bassPeak = absLow;
                if (absHigh > treblePeak) treblePeak = absHigh;
            }

            return (Math.Clamp(bassPeak, 0f, 1f), Math.Clamp(treblePeak, 0f, 1f));
        }

        /// Minimal one-pole (RC) low-pass filter, run continuously sample-by-sample
        /// across buffers so its internal state persists between reads.
        /// Good enough to separate "bass thump" from "everything else" for this purpose
        /// without pulling in a full FFT.
        private class OnePoleLowPass
        {
            private readonly float _alpha;
            private float _state;

            public OnePoleLowPass(float cutoffHz, float sampleRate)
            {
                float rc = 1f / (2f * MathF.PI * cutoffHz);
                float dt = 1f / sampleRate;
                _alpha = dt / (rc + dt);
            }

            public float Process(float x)
            {
                _state += _alpha * (x - _state);
                return _state;
            }
        }
    }
}