using System;
using System.Collections;
using System.Collections.Generic;

namespace Overtake.SimHub.Plugin.Finalizer
{
    /// <summary>
    /// Sanitizes numeric values before they enter the export JSON.
    /// JavaScriptSerializer emits the literal token "NaN" for non-finite
    /// doubles, which is invalid JSON and breaks downstream JSON.parse.
    /// Schema fields are number | null — use null when no valid sample exists.
    /// </summary>
    internal static class ExportNumbers
    {
        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        public static object RoundOrNull(double v, int decimals)
        {
            if (!IsFinite(v)) return null;
            return Math.Round(v, decimals);
        }

        public static object RoundOrNull(float v, int decimals)
        {
            return RoundOrNull((double)v, decimals);
        }

        public static object AverageOrNull(IList<float> values)
        {
            if (values == null || values.Count == 0) return null;
            double sum = 0d;
            int count = 0;
            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                if (!IsFinite(v)) continue;
                sum += v;
                count++;
            }
            if (count == 0) return null;
            return Math.Round(sum / count, 2);
        }

        public static List<object> RoundListOrNull(IList<float> values, double scale, int decimals)
        {
            var list = new List<object>();
            if (values == null) return list;
            for (int i = 0; i < values.Count; i++)
            {
                double v = (double)values[i] * scale;
                if (!IsFinite(v))
                    list.Add(null);
                else
                    list.Add(Math.Round(v, decimals));
            }
            return list;
        }

        public static List<object> RoundListOrNull(IList<double> values, int decimals)
        {
            var list = new List<object>();
            if (values == null) return list;
            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                if (!IsFinite(v))
                    list.Add(null);
                else
                    list.Add(Math.Round(v, decimals));
            }
            return list;
        }

        /// <summary>
        /// Last-line defense before JavaScriptSerializer: walk the export tree
        /// and replace any non-finite float/double with null (invalid JSON otherwise).
        /// </summary>
        public static void SanitizeForJson(object node)
        {
            if (node == null) return;

            var dict = node as Dictionary<string, object>;
            if (dict != null)
            {
                var keys = new List<string>(dict.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    object val = dict[key];
                    object fixedVal = SanitizeValue(val);
                    if (!ReferenceEquals(fixedVal, val))
                        dict[key] = fixedVal;
                    else
                        SanitizeForJson(val);
                }
                return;
            }

            var list = node as IList;
            if (list != null && !(node is string))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object val = list[i];
                    object fixedVal = SanitizeValue(val);
                    if (!ReferenceEquals(fixedVal, val))
                        list[i] = fixedVal;
                    else
                        SanitizeForJson(val);
                }
                return;
            }

            var arr = node as ArrayList;
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    object val = arr[i];
                    object fixedVal = SanitizeValue(val);
                    if (!ReferenceEquals(fixedVal, val))
                        arr[i] = fixedVal;
                    else
                        SanitizeForJson(val);
                }
            }
        }

        private static object SanitizeValue(object val)
        {
            if (val is float)
            {
                float f = (float)val;
                return IsFinite(f) ? (object)f : null;
            }
            if (val is double)
            {
                double d = (double)val;
                return IsFinite(d) ? (object)d : null;
            }
            return val;
        }
    }
}
