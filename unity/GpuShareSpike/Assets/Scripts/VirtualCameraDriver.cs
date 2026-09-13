// ============================================================================
//  Produce la pose che Unity manda a Unreal.
//
//  ------------------------------------------------------------------------
//  IL MODELLO, IN UNA FRASE
//  La tua camera di Unity e' il punto di vista. Il quad le e' figlio ed e' uno
//  schermo incollato davanti all'occhio. Muovendo la camera si muovono
//  entrambi, quindi il quad resta a riempire lo schermo: quello che cambia e'
//  il CONTENUTO della texture, perche' la pose della camera va a Unreal e
//  Unreal rirenderizza da li'.
//
//  Unity comanda la vista di Unreal, e con quella vista ci vede.
//  ------------------------------------------------------------------------
//
//  DUE ORIGINI CHE SI CORRISPONDONO
//  La pose spedita e' RELATIVA a OriginTransform (se non lo assegni, e'
//  relativa all'origine del mondo di Unity). Dall'altra parte, Unreal la
//  applica in relativo all'attore di cattura, che fa da ANCORA.
//
//      origine di Unity  ==  transform dell'attore ancora nel mondo Unreal
//
//  Quindi Unity lavora sempre in uno spazio locale piccolo, in metri e vicino
//  all'origine, mentre l'ancora lo colloca dove serve nel mondo enorme di
//  Unreal, e chi muove l'ancora si porta dietro tutto lo spazio di Unity.
//  E' lo stesso pattern del georeference di Cesium, ed e' il motivo per cui
//  la precisione in singola non degrada quando ti allontani.
//
//  MODALITA':
//    Manual             WASD + mouse. MUOVE SourceTransform, cioe' la tua
//                       camera vera.
//    DeterministicSweep oscillazione nota e ripetibile intorno alla posizione
//                       di partenza. MUOVE SourceTransform. E' il default
//                       perche' rende la latenza visibile a occhio come uno
//                       scostamento angolare costante, e confrontabile tra una
//                       misura e l'altra: col mouse in mano non confronti niente.
//    FollowTransform    non muove niente, LEGGE e basta. Usala quando a muovere
//                       la camera e' altro: un character controller, Cinemachine,
//                       un XR rig.
// ============================================================================

using UnityEngine;

namespace GpuShareSpike
{
    // -200: DEVE girare prima di ShareClient (che e' a -100), altrimenti
    // ShareClient spedisce la pose calcolata al frame PRECEDENTE e regala un
    // frame intero di latenza proprio allo strumento che serve a misurarla.
    [DefaultExecutionOrder(-200)]
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

        [Header("Punto di vista")]
        [Tooltip("Il Transform che E' il punto di vista. Lasciando vuoto, ShareClient ci mette la PresentCamera. Manual e Sweep lo MUOVONO; FollowTransform lo legge soltanto.")]
        public Transform SourceTransform;

        [Tooltip("Opzionale. Se assegnata, FOV verticale, near e far vengono presi da questa Camera invece che dai campi qui sotto.")]
        public Camera SourceCamera;

        [Tooltip("Opzionale. La pose spedita e' relativa a questo Transform; corrisponde all'attore ancora lato Unreal. Vuoto = origine del mondo di Unity.")]
        public Transform OriginTransform;

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

        [Header("Camera (usati se SourceCamera non e' assegnata)")]
        public float FovYDeg = 60.0f;
        [Tooltip("Metri.")] public float NearM = 0.1f;
        [Tooltip("Metri. 0 = infinito.")] public float FarM = 0.0f;

        [Tooltip("Posizione di partenza usata quando SourceTransform non e' assegnato.")]
        public Vector3 FallbackStartPosition = new Vector3(0.0f, 2.5f, -6.0f);

        /// <summary>Pose relativa a OriginTransform: e' questa che viene spedita.</summary>
        public Vector3 Position { get; private set; }
        public Quaternion Rotation { get; private set; }

        /// <summary>Pose in coordinate di mondo Unity, utile per diagnostica.</summary>
        public Vector3 WorldPosition { get; private set; }
        public Quaternion WorldRotation { get; private set; }

        private float _time;
        private float _manualYaw;
        private float _manualPitch;
        private Vector3 _fallbackPosition;
        private Quaternion _fallbackRotation = Quaternion.identity;
        private Vector3 _sweepBasePosition;
        private bool _sweepBaseCaptured;
        private bool _warnedAboutOriginScale;

        private void Awake()
        {
            _fallbackPosition = FallbackStartPosition;
            WorldPosition = _fallbackPosition;
            WorldRotation = Quaternion.identity;
            Position = WorldPosition;
            Rotation = WorldRotation;
        }

        private void Update()
        {
            // unscaledDeltaTime: Time.timeScale non deve poter alterare una misura.
            float deltaTime = Time.unscaledDeltaTime;
            _time += deltaTime;

            Vector3 worldPosition;
            Quaternion worldRotation;

            switch (Mode)
            {
                case DriveMode.DeterministicSweep:
                    StepSweep(out worldPosition, out worldRotation);
                    break;

                case DriveMode.Manual:
                    StepManual(deltaTime, out worldPosition, out worldRotation);
                    break;

                default: // FollowTransform
                    ReadSource(out worldPosition, out worldRotation);
                    break;
            }

            // Sweep e Manual scrivono davvero sulla camera: cosi' il quad, che le
            // e' figlio, la segue, e tutto il resto della scena Unity vede la
            // camera dove la vede Unreal.
            if (Mode != DriveMode.FollowTransform && SourceTransform != null)
            {
                SourceTransform.SetPositionAndRotation(worldPosition, worldRotation);
            }

            WorldPosition = worldPosition;
            WorldRotation = worldRotation;

            if (SourceCamera != null)
            {
                // Prendere i parametri dalla Camera vera evita che Unreal
                // renderizzi con un FOV diverso da quello che la logica di Unity
                // crede di avere.
                FovYDeg = SourceCamera.fieldOfView;   // Unity usa il VERTICALE
                NearM = SourceCamera.nearClipPlane;
                FarM = SourceCamera.farClipPlane;
            }

            ComputeRelativePose(worldPosition, worldRotation);
        }

        private void ReadSource(out Vector3 worldPosition, out Quaternion worldRotation)
        {
            if (SourceTransform != null)
            {
                // Transform di MONDO: se il tuo rig e' annidato, tiene conto dei parent.
                worldPosition = SourceTransform.position;
                worldRotation = SourceTransform.rotation;
            }
            else
            {
                worldPosition = _fallbackPosition;
                worldRotation = _fallbackRotation;
            }
        }

        private void StepSweep(out Vector3 worldPosition, out Quaternion worldRotation)
        {
            if (!_sweepBaseCaptured)
            {
                // Base dello sweep = dove hai messo la camera. Cosi' la inquadri
                // dove vuoi e l'oscillazione parte da li'.
                _sweepBasePosition = SourceTransform != null ? SourceTransform.position : _fallbackPosition;
                _sweepBaseCaptured = true;
            }

            float phase = _time * SweepHz * 2.0f * Mathf.PI;
            worldPosition = _sweepBasePosition + new Vector3(Mathf.Cos(phase * 0.5f) * SweepStrafeM, 0.0f, 0.0f);
            worldRotation = Quaternion.Euler(0.0f, Mathf.Sin(phase) * SweepYawAmplitudeDeg, 0.0f);

            _fallbackPosition = worldPosition;
            _fallbackRotation = worldRotation;
        }

        private void StepManual(float deltaTime, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            Vector3 current = SourceTransform != null ? SourceTransform.position : _fallbackPosition;

            _manualYaw += Input.GetAxis("Mouse X") * LookSensitivity;
            _manualPitch = Mathf.Clamp(_manualPitch - Input.GetAxis("Mouse Y") * LookSensitivity, -85.0f, 85.0f);
            worldRotation = Quaternion.Euler(_manualPitch, _manualYaw, 0.0f);

            var input = new Vector3(Input.GetAxis("Horizontal"), 0.0f, Input.GetAxis("Vertical"));
            worldPosition = current + worldRotation * input * (MoveSpeed * deltaTime);

            _fallbackPosition = worldPosition;
            _fallbackRotation = worldRotation;
            _sweepBaseCaptured = false;   // se torni allo sweep, riparte da qui
        }

        /// <summary>
        /// Porta la pose di mondo nello spazio dell'origine. E' l'esatto
        /// speculare di cio' che fa Unreal applicandola in relativo all'ancora.
        /// </summary>
        private void ComputeRelativePose(Vector3 worldPosition, Quaternion worldRotation)
        {
            if (OriginTransform == null)
            {
                Position = worldPosition;
                Rotation = worldRotation;
                return;
            }

            Vector3 scale = OriginTransform.lossyScale;
            if (!_warnedAboutOriginScale &&
                (Mathf.Abs(scale.x - 1.0f) > 0.001f || Mathf.Abs(scale.y - 1.0f) > 0.001f || Mathf.Abs(scale.z - 1.0f) > 0.001f))
            {
                // La scala e' deliberatamente ignorata: un'ancora scalata
                // renderebbe ambiguo il significato dei metri spediti a Unreal.
                Debug.LogWarning("[GpuShare] OriginTransform ha scala diversa da 1: viene ignorata. "
                               + "Usa un'ancora non scalata, altrimenti le distanze spedite a Unreal non sono metri.");
                _warnedAboutOriginScale = true;
            }

            Quaternion inverseOrigin = Quaternion.Inverse(OriginTransform.rotation);
            Position = inverseOrigin * (worldPosition - OriginTransform.position);
            Rotation = inverseOrigin * worldRotation;
        }

        /// <summary>Rapporto d'aspetto che entra nella pose.</summary>
        public float CurrentAspect()
        {
            // Se seguiamo una Camera vera, il suo aspect e' quello giusto:
            // potrebbe renderizzare su una RenderTexture di formato diverso
            // dalla finestra.
            if (SourceCamera != null && SourceCamera.aspect > 0.0f)
            {
                return SourceCamera.aspect;
            }
            return Screen.height > 0 ? (float)Screen.width / Screen.height : 16.0f / 9.0f;
        }
    }
}
