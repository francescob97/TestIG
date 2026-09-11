// ============================================================================
//  Genera le pose che Unity manda a Unreal.
//
//  Il default e' un movimento DETERMINISTICO, non un controllo libero.
//  Non e' pigrizia: con un movimento noto e ripetibile la latenza si vede a
//  occhio come uno scostamento angolare costante tra dove stai guardando e
//  dove l'immagine e' stata renderizzata, ed e' confrontabile tra una misura e
//  l'altra. Con il mouse in mano non confronti niente.
//
//  Il controllo manuale c'e' comunque, per le prove qualitative.
// ============================================================================

using UnityEngine;

namespace GpuShareSpike
{
    public sealed class VirtualCameraDriver : MonoBehaviour
    {
        public enum DriveMode
        {
            DeterministicSweep,
            Manual,
        }

        [Header("Modalita'")]
        public DriveMode Mode = DriveMode.DeterministicSweep;

        [Header("Sweep deterministico")]
        [Tooltip("Ampiezza dell'oscillazione di imbardata, in gradi.")]
        public float SweepYawAmplitudeDeg = 35.0f;

        [Tooltip("Frequenza dell'oscillazione, in Hz. Piu' e' alta, piu' la latenza e' visibile.")]
        public float SweepHz = 0.25f;

        [Tooltip("Ampiezza dello spostamento laterale, in metri. Serve a rendere visibile la parallasse.")]
        public float SweepStrafeM = 1.5f;

        [Header("Controllo manuale")]
        public float MoveSpeed = 4.0f;
        public float LookSensitivity = 2.0f;

        [Header("Camera")]
        public float FovYDeg = 60.0f;
        [Tooltip("Metri.")] public float NearM = 0.1f;
        [Tooltip("Metri. 0 = infinito.")] public float FarM = 0.0f;

        public Vector3 Origin = new Vector3(0.0f, 2.5f, -6.0f);

        public Vector3 Position { get; private set; }
        public Quaternion Rotation { get; private set; }

        private float _time;
        private float _manualYaw;
        private float _manualPitch;
        private Vector3 _manualPosition;

        private void Awake()
        {
            _manualPosition = Origin;
            Position = Origin;
            Rotation = Quaternion.identity;
        }

        private void Update()
        {
            // unscaledDeltaTime: Time.timeScale non deve poter alterare una misura.
            _time += Time.unscaledDeltaTime;

            if (Mode == DriveMode.DeterministicSweep)
            {
                float phase = _time * SweepHz * 2.0f * Mathf.PI;
                float yaw = Mathf.Sin(phase) * SweepYawAmplitudeDeg;
                float strafe = Mathf.Cos(phase * 0.5f) * SweepStrafeM;

                Position = Origin + new Vector3(strafe, 0.0f, 0.0f);
                Rotation = Quaternion.Euler(0.0f, yaw, 0.0f);
            }
            else
            {
                _manualYaw += Input.GetAxis("Mouse X") * LookSensitivity;
                _manualPitch = Mathf.Clamp(_manualPitch - Input.GetAxis("Mouse Y") * LookSensitivity, -85.0f, 85.0f);
                Rotation = Quaternion.Euler(_manualPitch, _manualYaw, 0.0f);

                var input = new Vector3(Input.GetAxis("Horizontal"), 0.0f, Input.GetAxis("Vertical"));
                _manualPosition += Rotation * input * (MoveSpeed * Time.unscaledDeltaTime);
                Position = _manualPosition;
            }
        }

        /// <summary>Rapporto d'aspetto corrente della finestra: entra nella pose.</summary>
        public float CurrentAspect()
        {
            return Screen.height > 0 ? (float)Screen.width / Screen.height : 16.0f / 9.0f;
        }
    }
}
