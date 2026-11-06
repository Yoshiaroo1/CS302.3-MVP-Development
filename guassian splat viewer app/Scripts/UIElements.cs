using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ForensicNeRF
{
    public enum IconKind
    {
        Select, Add, Minus, Measure, Crop, Pin, Logo, Caret, Close, Folder, File, Up, Trash, Focus
    }

    /// <summary>
    /// Resolution-independent line icons drawn with Painter2D, so the UI needs no image assets.
    /// The icon uses the element's text colour (the USS "color" property).
    /// </summary>
    public class VectorIcon : VisualElement
    {
        public IconKind Kind { get; private set; }

        public VectorIcon(IconKind kind, float size = 22f)
        {
            Kind = kind;
            pickingMode = PickingMode.Ignore;
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            AddToClassList("vector-icon");
            generateVisualContent += Draw;
        }

        public void SetKind(IconKind kind)
        {
            Kind = kind;
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            float s = Mathf.Min(r.width, r.height);
            if (s <= 0f) return;
            Vector2 o = new Vector2(r.x + (r.width - s) * 0.5f, r.y + (r.height - s) * 0.5f);
            Vector2 P(float x, float y) => new Vector2(o.x + x * s, o.y + y * s);

            var p = ctx.painter2D;
            Color c = resolvedStyle.color;
            p.strokeColor = c;
            p.fillColor = c;
            p.lineWidth = Mathf.Max(1.4f, s * 0.075f);
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            switch (Kind)
            {
                case IconKind.Select:
                    Poly(p, true, P(0.27f, 0.14f), P(0.27f, 0.80f), P(0.43f, 0.65f), P(0.54f, 0.88f),
                        P(0.64f, 0.84f), P(0.53f, 0.61f), P(0.75f, 0.60f));
                    break;
                case IconKind.Add:
                    Line(p, P(0.5f, 0.16f), P(0.5f, 0.84f));
                    Line(p, P(0.16f, 0.5f), P(0.84f, 0.5f));
                    break;
                case IconKind.Minus:
                    Line(p, P(0.16f, 0.5f), P(0.84f, 0.5f));
                    break;
                case IconKind.Measure:
                    Line(p, P(0.27f, 0.27f), P(0.64f, 0.64f));
                    Circle(p, P(0.22f, 0.22f), s * 0.08f, false);
                    Circle(p, P(0.72f, 0.72f), s * 0.13f, true);
                    break;
                case IconKind.Crop:
                    Poly(p, false, P(0.30f, 0.10f), P(0.30f, 0.70f), P(0.90f, 0.70f));
                    Poly(p, false, P(0.10f, 0.30f), P(0.70f, 0.30f), P(0.70f, 0.90f));
                    break;
                case IconKind.Pin:
                    p.BeginPath();
                    p.MoveTo(P(0.5f, 0.92f));
                    p.BezierCurveTo(P(0.14f, 0.55f), P(0.14f, 0.10f), P(0.5f, 0.10f));
                    p.BezierCurveTo(P(0.86f, 0.10f), P(0.86f, 0.55f), P(0.5f, 0.92f));
                    p.ClosePath();
                    p.Stroke();
                    Circle(p, P(0.5f, 0.40f), s * 0.10f, false);
                    break;
                case IconKind.Logo:
                    Circle(p, P(0.5f, 0.5f), s * 0.44f, false);
                    Poly(p, true, P(0.5f, 0.28f), P(0.30f, 0.65f), P(0.70f, 0.65f));
                    Circle(p, P(0.5f, 0.28f), s * 0.075f, true);
                    Circle(p, P(0.30f, 0.65f), s * 0.075f, true);
                    Circle(p, P(0.70f, 0.65f), s * 0.075f, true);
                    break;
                case IconKind.Caret:
                    Poly(p, false, P(0.25f, 0.38f), P(0.5f, 0.63f), P(0.75f, 0.38f));
                    break;
                case IconKind.Close:
                    Line(p, P(0.25f, 0.25f), P(0.75f, 0.75f));
                    Line(p, P(0.75f, 0.25f), P(0.25f, 0.75f));
                    break;
                case IconKind.Folder:
                    Poly(p, true, P(0.10f, 0.24f), P(0.40f, 0.24f), P(0.48f, 0.34f), P(0.90f, 0.34f),
                        P(0.90f, 0.82f), P(0.10f, 0.82f));
                    break;
                case IconKind.File:
                    Poly(p, true, P(0.24f, 0.10f), P(0.60f, 0.10f), P(0.80f, 0.30f), P(0.80f, 0.90f), P(0.24f, 0.90f));
                    Circle(p, P(0.42f, 0.52f), s * 0.06f, true);
                    Circle(p, P(0.60f, 0.62f), s * 0.05f, true);
                    Circle(p, P(0.46f, 0.73f), s * 0.04f, true);
                    break;
                case IconKind.Up:
                    Line(p, P(0.5f, 0.82f), P(0.5f, 0.20f));
                    Poly(p, false, P(0.26f, 0.44f), P(0.5f, 0.20f), P(0.74f, 0.44f));
                    break;
                case IconKind.Trash:
                    Line(p, P(0.18f, 0.26f), P(0.82f, 0.26f));
                    Poly(p, false, P(0.40f, 0.26f), P(0.40f, 0.14f), P(0.60f, 0.14f), P(0.60f, 0.26f));
                    Poly(p, false, P(0.28f, 0.26f), P(0.32f, 0.88f), P(0.68f, 0.88f), P(0.72f, 0.26f));
                    break;
                case IconKind.Focus:
                    Circle(p, P(0.5f, 0.5f), s * 0.26f, false);
                    Line(p, P(0.5f, 0.06f), P(0.5f, 0.24f));
                    Line(p, P(0.5f, 0.76f), P(0.5f, 0.94f));
                    Line(p, P(0.06f, 0.5f), P(0.24f, 0.5f));
                    Line(p, P(0.76f, 0.5f), P(0.94f, 0.5f));
                    break;
            }
        }

        static void Line(Painter2D p, Vector2 a, Vector2 b)
        {
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.Stroke();
        }

        static void Poly(Painter2D p, bool closed, params Vector2[] pts)
        {
            p.BeginPath();
            p.MoveTo(pts[0]);
            for (int i = 1; i < pts.Length; i++) p.LineTo(pts[i]);
            if (closed) p.ClosePath();
            p.Stroke();
        }

        static void Circle(Painter2D p, Vector2 c, float radius, bool fill)
        {
            p.BeginPath();
            p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            p.ClosePath();
            if (fill) p.Fill(); else p.Stroke();
        }
    }

    /// <summary>An element that lets a callback draw on it every repaint.</summary>
    public class CanvasElement : VisualElement
    {
        public Action<Painter2D> OnDraw;

        public CanvasElement()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += ctx => OnDraw?.Invoke(ctx.painter2D);
        }
    }

    /// <summary>
    /// The X/Y/Z orientation widget in the corner of the viewport. Click an axis to look along it.
    /// </summary>
    public class AxisGizmo : VisualElement
    {
        public Action<Vector3> AxisClicked;

        static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };
        static readonly Color[] Colors =
        {
            new Color(0.90f, 0.29f, 0.30f),
            new Color(0.24f, 0.77f, 0.42f),
            new Color(0.26f, 0.53f, 0.96f)
        };
        static readonly string[] Names = { "x", "y", "z" };

        readonly Label[] labels = new Label[3];
        Quaternion camRotation = Quaternion.identity;
        bool hasCamera;

        public AxisGizmo()
        {
            AddToClassList("axis-gizmo");
            for (int i = 0; i < 3; i++)
            {
                labels[i] = new Label(Names[i]);
                labels[i].AddToClassList("axis-label");
                labels[i].style.color = Colors[i];
                labels[i].pickingMode = PickingMode.Ignore;
                Add(labels[i]);
            }
            generateVisualContent += Draw;
            RegisterCallback<PointerDownEvent>(OnPointerDown);
        }

        Vector2 Center => new Vector2(layout.width * 0.5f, layout.height * 0.5f);
        float Radius => Mathf.Min(layout.width, layout.height) * 0.30f;

        Vector3 ViewDir(int axis, float sign) => Quaternion.Inverse(camRotation) * (Axes[axis] * sign);

        Vector2 ScreenEnd(Vector3 viewDir, float radius) => Center + new Vector2(viewDir.x, -viewDir.y) * radius;

        /// <summary>Call once per frame with the viewing camera.</summary>
        public void Refresh(Camera cam)
        {
            hasCamera = cam != null;
            if (!hasCamera || float.IsNaN(layout.width)) return;
            camRotation = cam.transform.rotation;
            for (int i = 0; i < 3; i++)
            {
                Vector2 lp = ScreenEnd(ViewDir(i, 1f), Radius + 12f);
                labels[i].style.left = lp.x;
                labels[i].style.top = lp.y;
            }
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            if (!hasCamera) return;
            var p = ctx.painter2D;
            p.lineCap = LineCap.Round;
            Vector2 c = Center;
            float r = Radius;

            // Draw the axes pointing away from the camera first.
            int[] order = { 0, 1, 2 };
            Array.Sort(order, (a, b) => ViewDir(b, 1f).z.CompareTo(ViewDir(a, 1f).z));

            for (int i = 0; i < 3; i++)
            {
                Vector2 back = ScreenEnd(ViewDir(i, -1f), r);
                Color faded = Colors[i];
                faded.a = 0.35f;
                Dot(p, back, 2.5f, faded);
            }

            foreach (int i in order)
            {
                Vector2 end = ScreenEnd(ViewDir(i, 1f), r);
                p.strokeColor = Colors[i];
                p.lineWidth = 2f;
                p.BeginPath();
                p.MoveTo(c);
                p.LineTo(end);
                p.Stroke();
                Dot(p, end, 3.5f, Colors[i]);
            }

            Dot(p, c, 3f, new Color(0.9f, 0.93f, 0.96f));
        }

        static void Dot(Painter2D p, Vector2 c, float radius, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            p.ClosePath();
            p.Fill();
        }

        void OnPointerDown(PointerDownEvent evt)
        {
            if (!hasCamera || evt.button != 0) return;
            Vector2 local = this.WorldToLocal(evt.position);
            float best = 18f * 18f;
            Vector3 bestAxis = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector2 end = ScreenEnd(ViewDir(i, s), Radius + (s > 0 ? 6f : 0f));
                    float d = (end - local).sqrMagnitude;
                    if (d < best) { best = d; bestAxis = Axes[i] * s; }
                }
            }
            if (bestAxis != Vector3.zero) AxisClicked?.Invoke(bestAxis);
            evt.StopPropagation();
        }
    }

    /// <summary>Small helpers for building UI from code.</summary>
    public static class UIBuild
    {
        public static VisualElement Div(VisualElement parent, params string[] classes)
        {
            var e = new VisualElement();
            foreach (var c in classes) e.AddToClassList(c);
            parent?.Add(e);
            return e;
        }

        public static Label Text(VisualElement parent, string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            parent?.Add(l);
            return l;
        }

        /// <summary>A text button. Uses a Label with a click handler so theme button styles don't interfere.</summary>
        public static Label TextButton(VisualElement parent, string text, Action onClick, params string[] classes)
        {
            var l = Text(parent, text, classes);
            l.AddToClassList("btn");
            l.AddManipulator(new Clickable(onClick));
            return l;
        }

        public static VisualElement IconButton(VisualElement parent, IconKind kind, float iconSize, Action onClick, params string[] classes)
        {
            var e = Div(parent, classes);
            e.AddToClassList("btn");
            e.Add(new VectorIcon(kind, iconSize));
            e.AddManipulator(new Clickable(onClick));
            return e;
        }

        /// <summary>Re-draws vector icons inside an element after its colour state changes.</summary>
        public static void RepaintIcons(VisualElement e)
        {
            e?.Query<VectorIcon>().ForEach(i => i.MarkDirtyRepaint());
        }
    }
}
