using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Notes.Services;

public class AudioRecordingService
{
    private MediaCapture _mediaCapture;
    private StorageFile _recordingFile;
    private bool _isRecording;

    public event EventHandler<bool> RecordingStateChanged;

    public bool IsRecording => _isRecording;

    public async Task<bool> InitializeAsync()
    {
        try
        {
            Debug.WriteLine("[AudioRecordingService] Starting MediaCapture initialization...");
            _mediaCapture = new MediaCapture();
            MediaCaptureInitializationSettings settings = new()
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio
            };

            await _mediaCapture.InitializeAsync(settings);
            Debug.WriteLine("[AudioRecordingService] MediaCapture initialization successful");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioRecordingService] ERROR: Failed to initialize MediaCapture: {ex.Message}");
            Debug.WriteLine($"[AudioRecordingService] Exception details: {ex}");
            return false;
        }
    }

    public async Task<StorageFile> StartRecordingAsync()
    {
        if (_isRecording || _mediaCapture == null)
        {
            Debug.WriteLine($"[AudioRecordingService] Cannot start recording - IsRecording: {_isRecording}, MediaCapture: {(_mediaCapture == null ? "null" : "initialized")}");
            return null;
        }

        try
        {
            Debug.WriteLine("[AudioRecordingService] Starting audio recording...");
            StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            string fileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
            Debug.WriteLine($"[AudioRecordingService] Recording to file: {fileName}");

            _recordingFile = await attachmentsFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
            Debug.WriteLine($"[AudioRecordingService] Recording file created at: {_recordingFile.Path}");

            MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
            profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            Debug.WriteLine("[AudioRecordingService] Audio encoding profile configured (16kHz, mono, 16-bit)");

            await _mediaCapture.StartRecordToStorageFileAsync(profile, _recordingFile);
            _isRecording = true;
            RecordingStateChanged?.Invoke(this, true);
            Debug.WriteLine("[AudioRecordingService] Recording started successfully");

            return _recordingFile;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioRecordingService] ERROR: Failed to start recording: {ex.Message}");
            Debug.WriteLine($"[AudioRecordingService] Exception details: {ex}");
            _isRecording = false;
            RecordingStateChanged?.Invoke(this, false);
            return null;
        }
    }

    public async Task<StorageFile> StopRecordingAsync()
    {
        if (!_isRecording || _mediaCapture == null)
        {
            Debug.WriteLine($"[AudioRecordingService] Cannot stop recording - IsRecording: {_isRecording}, MediaCapture: {(_mediaCapture == null ? "null" : "initialized")}");
            return null;
        }

        try
        {
            Debug.WriteLine("[AudioRecordingService] Stopping audio recording...");
            await _mediaCapture.StopRecordAsync();
            _isRecording = false;
            RecordingStateChanged?.Invoke(this, false);
            Debug.WriteLine($"[AudioRecordingService] Recording stopped successfully. File: {_recordingFile?.Path}");

            return _recordingFile;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioRecordingService] ERROR: Failed to stop recording: {ex.Message}");
            Debug.WriteLine($"[AudioRecordingService] Exception details: {ex}");
            _isRecording = false;
            RecordingStateChanged?.Invoke(this, false);
            return null;
        }
    }

    public void Dispose()
    {
        Debug.WriteLine("[AudioRecordingService] Disposing AudioRecordingService...");
        if (_isRecording)
        {
            Debug.WriteLine("[AudioRecordingService] Stopping active recording during disposal");
            _ = StopRecordingAsync();
        }

        _mediaCapture?.Dispose();
        _mediaCapture = null;
        Debug.WriteLine("[AudioRecordingService] AudioRecordingService disposed");
    }
}