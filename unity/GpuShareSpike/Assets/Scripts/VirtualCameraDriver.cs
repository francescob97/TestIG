// ============================================================================
//  Genera le pose che Unity manda a Unreal. E' LA camera virtuale: la camera
//  di Unreal si mette esattamente dove dice questo componente.
//
//  ATTENZIONE ALLA DISTINZIONE PIU' CONFONDENTE DI TUTTO IL PROGETTO.
//  In scena ci sono DUE cose che si chiamano "camera" e non c'entrano niente
//  l'una con l'altra:
//
//    1. LA CAMERA VIRTUALE (questo componente)
//       Non renderizza nulla. E' solo una posizione + rotazione che viene
//       spedita a Unreal via UDP. E' il punto di vista nel mondo 3D.
//
//    2. LA PRESENT CAMERA (ShareClient.PresentCamera)
//       E' ortografica e serve SOLO a disegnare il quad con la texture che
//       arriva da Unreal. Non ha niente a che vedere col punto di vista 3D.
//
//  NON far seguire a FollowTransform la PresentCamera: il quad le e' figlio,
//  quindi si muoverebbe insieme a lei e a schermo non cambierebbe nulla.
//  Usa un GameObject separato (un rig, un player controller, l'XR rig...).
//
//  MODALITA':
//    DeterministicSweep  oscillazione nota e ripetibile. E' il default perche'
//                        rende la latenza visibile a occhio come uno
//                        scostamento angolare costante, e confrontabile tra
//                        una misura e l'altra. Col mouse in mano non confronti
//                        niente.
//    Manual              WASD + mouse, per le prove qualitative.
//    FollowTransform     segue un Transform di Unity. E' la modalita' da usare
//                        quando vuoi che la camera di Unreal stia esattamente
//                        dove sta un oggetto della tua scena Unity.
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
            FollowTransform,
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

        [Header("Follow transform")]
        [Tooltip("Il Transform che la camera di Unreal deve seguire. NON usare la PresentCamera: il quad le e' figlio.")]
        public Transform SourceTransform;

        [Tooltip("Opzionale. Se assegnata, FOV verticale, near e far vengono presi da questa Camera invece che dai campi qui sotto.")]
        public Camera SourceCamera;

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
            else if (Mode == DriveMode.FollowTransform)
            {
                if (SourceTransform != null)
                {
                    // Trasformata di MONDO: Unreal riceve una pose assoluta.
                    // Se il tuo rig e' annidato, questo tiene conto dei parent.
                    Position = SourceTransform.position;
                    Rotation = SourceTransform.rotation;
                }

                if (SourceCamera != null)
                {
                    // Prendere i parametri dalla Camera vera evita che l'immagine
                    // di Unreal sia renderizzata con un FOV diverso da quello che
                    // la logica di Unity crede di avere.
                    FovYDeg = SourceCamera.fieldOfView;   // Unity usa il VERTICALE
                    NearM = SourceCamera.nearClipPlane;
                    FarM = SourceCamera.farClipPlane;
                }
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

        /// <summary>Rapporto d'aspetto che entra nella pose.</summary>
        public float CurrentAspect()
        {
            // Se stiamo seguendo una Camera vera, il suo aspect e' quello giusto:
            // potrebbe renderizzare su una RenderTexture di formato diverso dalla
            // finestra.
            if (Mode == DriveMode.FollowTransform && SourceCamera != null && SourceCamera.aspect > 0.0f)
            {
                return SourceCamera.aspect;
            }
            return Screen.height > 0 ? (float)Screen.width / Screen.height : 16.0f / 9.0f;
        }
    }
}
