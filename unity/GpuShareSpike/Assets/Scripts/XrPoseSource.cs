// ============================================================================
//  Pose e frustum di ciascun occhio del visore.
//
//  TRE FONTI, IN ORDINE DI AFFIDABILITA' (l'HUD dice quale sta usando):
//
//  1. XRDisplaySubsystem, render parameter per occhio.
//     E' ESATTAMENTE cio' che URP usa per renderizzare ogni occhio (view e
//     projection per occhio), quindi quello che mandiamo a Unreal combacia per
//     costruzione con quello che il visore mostrera'.
//
//  2. Camera.GetStereoViewMatrix / GetStereoProjectionMatrix.
//     L'API classica. Di solito coincide con la 1.
//
//  3. Ripiego: testa + distanza interpupillare di 64 mm, frustum della camera.
//     Serve solo a non restare al buio: il frustum NON e' quello del visore e
//     l'immagine non combacera' con gli occhi. Se vedi questa fonte in HUD, c'e'
//     qualcosa da sistemare nella configurazione XR.
//
//  CONVENZIONI (tutte Unity, nessuna matrice di Unreal entra qui):
//    view       = mondo -> occhio, convenzione GL di Unity (l'occhio guarda -Z)
//    projection = convenzione GL di Unity (quella di Camera.projectionMatrix)
// ============================================================================

using System;
using UnityEngine;
using UnityEngine.XR;

namespace GpuShareSpike
{
    public sealed class XrEyes
    {
        public int Count;
        public readonly Matrix4x4[] View = new Matrix4x4[2];
        public readonly Matrix4x4[] Projection = new Matrix4x4[2];
        public string Source = "-";
        public bool IsFallback;
    }

    public static class XrPoseSource
    {
        private const float FallbackIpdMeters = 0.064f;

        public static bool TryGetEyes(Camera camera, XrEyes eyes)
        {
            eyes.Count = 0;
            eyes.IsFallback = false;
            if (camera == null) return false;

            if (TryFromDisplay(camera, eyes))
            {
                eyes.Source = "XRDisplaySubsystem";
                return true;
            }
            if (TryFromCamera(camera, eyes))
            {
                eyes.Source = "Camera.GetStereo*Matrix";
                return true;
            }

            FromHeadAndIpd(camera, eyes);
            eyes.Source = "RIPIEGO: testa + IPD 64 mm (frustum NON del visore)";
            eyes.IsFallback = true;
            return true;
        }

        private static bool TryFromDisplay(Camera camera, XrEyes eyes)
        {
            XRDisplaySubsystem display = XrRuntime.ActiveDisplay;
            if (display == null) return false;

            try
            {
                // Single Pass Instanced: 1 render pass con 2 parametri.
                // Multi Pass:            2 render pass con 1 parametro ciascuno.
                // In entrambi i casi l'ordine e' sinistro, destro.
                int found = 0;
                int passCount = display.GetRenderPassCount();
                for (int p = 0; p < passCount && found < 2; ++p)
                {
                    display.GetRenderPass(p, out XRDisplaySubsystem.XRRenderPass pass);
                    int paramCount = pass.GetRenderParameterCount();
                    for (int i = 0; i < paramCount && found < 2; ++i)
                    {
                        pass.GetRenderParameter(camera, i, out XRDisplaySubsystem.XRRenderParameter parameter);
                        eyes.View[found] = parameter.view;
                        eyes.Projection[found] = parameter.projection;
                        ++found;
                    }
                }

                eyes.Count = found;
                return found == 2 && IsPlausible(eyes);
            }
            catch (Exception)
            {
                // Chiamata fuori dal momento in cui il display ha i suoi pass
                // pronti: si ripiega sulla fonte successiva.
                return false;
            }
        }

        private static bool TryFromCamera(Camera camera, XrEyes eyes)
        {
            if (!camera.stereoEnabled) return false;

            eyes.View[0] = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
            eyes.View[1] = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
            eyes.Projection[0] = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
            eyes.Projection[1] = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
            eyes.Count = 2;
            return IsPlausible(eyes);
        }

        private static void FromHeadAndIpd(Camera camera, XrEyes eyes)
        {
            Transform head = camera.transform;
            Vector3 halfIpd = head.right * (FallbackIpdMeters * 0.5f);
            eyes.View[0] = ViewMath.ViewFromPose(head.position - halfIpd, head.rotation);
            eyes.View[1] = ViewMath.ViewFromPose(head.position + halfIpd, head.rotation);
            eyes.Projection[0] = camera.projectionMatrix;
            eyes.Projection[1] = camera.projectionMatrix;
            eyes.Count = 2;
        }

        /// <summary>Scarta dati chiaramente sbagliati (matrici nulle, occhi coincidenti, frustum degeneri).</summary>
        private static bool IsPlausible(XrEyes eyes)
        {
            ViewMath.PoseFromView(eyes.View[0], out Vector3 left, out _);
            ViewMath.PoseFromView(eyes.View[1], out Vector3 right, out _);
            float separation = Vector3.Distance(left, right);
            if (separation < 0.01f || separation > 0.2f) return false;

            for (int i = 0; i < 2; ++i)
            {
                ViewMath.TangentsFromProjection(eyes.Projection[i], out float l, out float r, out float d, out float u);
                if (!(r > l) || !(u > d) || float.IsNaN(l) || float.IsNaN(u)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Conversioni tra pose, matrici e tangenti del frustum, tutte in
    /// convenzione Unity. Sono il ponte tra cio' che dice il visore e cio' che
    /// spediamo a Unreal (tangenti) o usiamo per riproiettare (matrici).
    /// </summary>
    public static class ViewMath
    {
        private static readonly Matrix4x4 FlipZ = Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f));

        /// <summary>Mondo -> occhio, convenzione GL di Unity (come Camera.worldToCameraMatrix).</summary>
        public static Matrix4x4 ViewFromPose(Vector3 position, Quaternion rotation)
        {
            return FlipZ * Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
        }

        /// <summary>
        /// Inversa di ViewFromPose. La view di Unity guarda verso -Z, la
        /// transform verso +Z: da qui la moltiplicazione per FlipZ.
        /// LookRotation invece di Matrix4x4.rotation: e' robusta a piccole
        /// imprecisioni numeriche che renderebbero la matrice non ortonormale.
        /// </summary>
        public static void PoseFromView(Matrix4x4 view, out Vector3 position, out Quaternion rotation)
        {
            Matrix4x4 localToWorld = view.inverse * FlipZ;
            position = localToWorld.GetColumn(3);
            Vector3 forward = localToWorld.GetColumn(2);
            Vector3 up = localToWorld.GetColumn(1);
            rotation = forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward, up)
                : Quaternion.identity;
        }

        /// <summary>
        /// Tangenti del frustum da una proiezione GL (anche off-center):
        ///   P00 = 2/(R-L)   P02 = (R+L)/(R-L)   ->  R = (P02+1)/P00,  L = (P02-1)/P00
        ///   P11 = 2/(U-D)   P12 = (U+D)/(U-D)   ->  U = (P12+1)/P11,  D = (P12-1)/P11
        /// </summary>
        public static void TangentsFromProjection(Matrix4x4 p, out float left, out float right, out float down, out float up)
        {
            left  = (p.m02 - 1.0f) / p.m00;
            right = (p.m02 + 1.0f) / p.m00;
            down  = (p.m12 - 1.0f) / p.m11;
            up    = (p.m12 + 1.0f) / p.m11;
        }

        /// <summary>
        /// Proiezione GL off-center dalle tangenti. Per la riproiezione contano
        /// solo x, y e w del clip: near e far non influiscono sul risultato.
        /// </summary>
        public static Matrix4x4 ProjectionFromTangents(float left, float right, float down, float up, float near, float far)
        {
            var m = Matrix4x4.zero;
            m.m00 = 2.0f / (right - left);
            m.m02 = (right + left) / (right - left);
            m.m11 = 2.0f / (up - down);
            m.m12 = (up + down) / (up - down);
            m.m22 = -(far + near) / (far - near);
            m.m23 = -(2.0f * far * near) / (far - near);
            m.m32 = -1.0f;
            return m;
        }

        /// <summary>
        /// Allarga il frustum di un margine angolare su ogni lato. E' il bordo
        /// extra che la riproiezione usa quando la testa si e' girata dopo che
        /// Unreal ha renderizzato: senza, ai bordi comparirebbe il nero.
        /// </summary>
        public static void ExpandTangents(ref float left, ref float right, ref float down, ref float up, float marginDeg)
        {
            if (marginDeg <= 0.0f) return;
            float m = marginDeg * Mathf.Deg2Rad;
            const float limit = 88.0f * Mathf.Deg2Rad;
            left  = Mathf.Tan(Mathf.Max(Mathf.Atan(left)  - m, -limit));
            right = Mathf.Tan(Mathf.Min(Mathf.Atan(right) + m,  limit));
            down  = Mathf.Tan(Mathf.Max(Mathf.Atan(down)  - m, -limit));
            up    = Mathf.Tan(Mathf.Min(Mathf.Atan(up)    + m,  limit));
        }

        /// <summary>
        /// Porta una pose di mondo nello spazio dell'origine (Unity) che
        /// corrisponde all'attore ancora lato Unreal. La scala e' ignorata di
        /// proposito: un'ancora scalata renderebbe ambiguo il significato dei
        /// metri spediti.
        /// </summary>
        public static void ToOriginSpace(Transform origin, Vector3 worldPosition, Quaternion worldRotation,
                                         out Vector3 position, out Quaternion rotation)
        {
            if (origin == null)
            {
                position = worldPosition;
                rotation = worldRotation;
                return;
            }
            Quaternion inverseOrigin = Quaternion.Inverse(origin.rotation);
            position = inverseOrigin * (worldPosition - origin.position);
            rotation = inverseOrigin * worldRotation;
        }
    }
}
