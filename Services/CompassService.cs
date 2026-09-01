#if ANDROID
using Android.Hardware;

namespace UCNLLauncher.Services;

public class CompassService
{
    private SensorManager? _sensorManager;
    private Sensor? _accelerometer;
    private Sensor? _magnetometer;
    private float[] _gravity = new float[3];
    private float[] _geomagnetic = new float[3];
    private bool _hasGravity = false;
    private bool _hasGeomagnetic = false;
    private readonly Action<float> _onHeadingChanged;
    private SensorEventListener? _listener;

    public CompassService(Action<float> onHeadingChanged)
    {
        _onHeadingChanged = onHeadingChanged;
        _sensorManager = (SensorManager?)Android.App.Application.Context
            .GetSystemService(Android.Content.Context.SensorService);

        if (_sensorManager != null)
        {
            _accelerometer = _sensorManager.GetDefaultSensor(SensorType.Accelerometer);
            _magnetometer = _sensorManager.GetDefaultSensor(SensorType.MagneticField);
        }
    }

    public bool IsAvailable => _sensorManager != null && _accelerometer != null && _magnetometer != null;

    public void Start()
    {
        if (!IsAvailable) return;

        _listener = new SensorEventListener(OnSensorChanged);

        _sensorManager!.RegisterListener(
            _listener,
            _accelerometer,
            SensorDelay.Ui);

        _sensorManager.RegisterListener(
            _listener,
            _magnetometer,
            SensorDelay.Ui);
    }

    public void Stop()
    {
        if (_sensorManager != null && _listener != null)
        {
            _sensorManager.UnregisterListener(_listener);
            _listener = null;
        }
    }

    private void OnSensorChanged(SensorEvent e)
    {
        if (e.Sensor?.Type == SensorType.Accelerometer)
        {
            _gravity = e.Values.ToArray();
            _hasGravity = true;
        }
        else if (e.Sensor?.Type == SensorType.MagneticField)
        {
            _geomagnetic = e.Values.ToArray();
            _hasGeomagnetic = true;
        }

        if (_hasGravity && _hasGeomagnetic)
        {
            float[] R = new float[9];
            float[] I = new float[9];

            if (SensorManager.GetRotationMatrix(R, I, _gravity, _geomagnetic))
            {
                float[] orientation = new float[3];
                SensorManager.GetOrientation(R, orientation);

                float azimuth = orientation[0] * 180 / (float)Math.PI;
                float heading = (azimuth + 360) % 360;

                _onHeadingChanged?.Invoke(heading);
            }
        }
    }

    private class SensorEventListener : Java.Lang.Object, ISensorEventListener
    {
        private readonly Action<SensorEvent> _callback;

        public SensorEventListener(Action<SensorEvent> callback)
        {
            _callback = callback;
        }

        public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy) { }

        public void OnSensorChanged(SensorEvent e)
        {
            _callback?.Invoke(e);
        }
    }
}
#endif