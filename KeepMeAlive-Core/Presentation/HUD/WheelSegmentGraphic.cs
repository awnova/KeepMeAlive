//====================[ Imports ]====================
using UnityEngine;
using UnityEngine.UI;

namespace KeepMeAlive.Presentation.HUD
{
    //====================[ WheelSegmentGraphic ]====================
    // A flat ring sector drawn as a mesh around its rect's pivot: inner and outer radius, and a start angle and sweep in
    // degrees measured clockwise from straight up (the wheel's slot convention). Inner radius 0 and a 360 degree sweep
    // make a disc. Used for the wheel's segments, the highlighted segment's wash and green rim, and the hub.
    internal sealed class WheelSegmentGraphic : MaskableGraphic
    {
        // About one quad per 2 degrees keeps the 340 px outer edge smooth at 4K.
        private const float DegreesPerStep = 2f;

        private float _inner, _outer, _start, _sweep;

        public void SetShape(float inner, float outer, float startDegrees, float sweepDegrees)
        {
            if (Mathf.Approximately(inner, _inner) && Mathf.Approximately(outer, _outer)
                && Mathf.Approximately(startDegrees, _start) && Mathf.Approximately(sweepDegrees, _sweep)) return;
            _inner = inner;
            _outer = outer;
            _start = startDegrees;
            _sweep = sweepDegrees;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_outer <= _inner || _sweep <= 0f) return;

            int steps = Mathf.Max(1, Mathf.CeilToInt(_sweep / DegreesPerStep));
            var vertex = UIVertex.simpleVert;
            vertex.color = color;

            for (int i = 0; i <= steps; i++)
            {
                Vector2 dir = Direction(_start + _sweep * i / steps);
                vertex.position = dir * _inner;
                vh.AddVert(vertex);
                vertex.position = dir * _outer;
                vh.AddVert(vertex);

                if (i == 0) continue;
                int b = i * 2;
                vh.AddTriangle(b - 2, b - 1, b + 1);
                vh.AddTriangle(b - 2, b + 1, b);
            }
        }

        // Unit vector at an angle clockwise from up.
        internal static Vector2 Direction(float degrees)
        {
            float rad = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }
    }

    //====================[ WheelPointerGraphic ]====================
    // The small triangle inside the hub's edge that shows where the pointer is aimed: its tip at one radius, its base at a
    // smaller one, spread a few degrees either side.
    internal sealed class WheelPointerGraphic : MaskableGraphic
    {
        private float _angle, _tip, _base, _halfSpread;

        public void SetShape(float angleDegrees, float tipRadius, float baseRadius, float halfSpreadDegrees)
        {
            if (Mathf.Approximately(angleDegrees, _angle) && Mathf.Approximately(tipRadius, _tip)
                && Mathf.Approximately(baseRadius, _base) && Mathf.Approximately(halfSpreadDegrees, _halfSpread)) return;
            _angle = angleDegrees;
            _tip = tipRadius;
            _base = baseRadius;
            _halfSpread = halfSpreadDegrees;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_tip <= 0f) return;

            var vertex = UIVertex.simpleVert;
            vertex.color = color;
            vertex.position = WheelSegmentGraphic.Direction(_angle) * _tip;
            vh.AddVert(vertex);
            vertex.position = WheelSegmentGraphic.Direction(_angle - _halfSpread) * _base;
            vh.AddVert(vertex);
            vertex.position = WheelSegmentGraphic.Direction(_angle + _halfSpread) * _base;
            vh.AddVert(vertex);
            vh.AddTriangle(0, 1, 2);
        }
    }
}
