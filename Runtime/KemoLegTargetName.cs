using System.Text;

namespace Paratap.KemoLegAim
{
    /// <summary>
    /// ターゲット用空オブジェクトの名前生成ロジック。
    /// 仕様 §6: アッパースネークケースをベースに、判別しやすい接頭辞 AIMTARGET_ を付ける。
    /// 例: DummyUpperLeg_L -> AIMTARGET_DUMMY_UPPER_LEG_L
    /// Runtime / Editor 双方から使うため Runtime 側に置く。
    /// </summary>
    public static class KemoLegTargetName
    {
        public const string Prefix = "AIMTARGET_";

        /// <summary>ボーン名からターゲット名を生成する。ボーン名が空なら既定名を返す。</summary>
        public static string Generate(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return Prefix + "TARGET";
            return Prefix + ToUpperSnake(boneName);
        }

        /// <summary>
        /// CamelCase / PascalCase 混じりの名前を UPPER_SNAKE_CASE に変換する。
        /// 区切り文字 (_ . - 空白) は '_' に正規化し、連続する '_' は1つにまとめる。
        /// </summary>
        public static string ToUpperSnake(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var sb = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];

                if (c == '_' || c == '-' || c == '.' || c == ' ')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                    continue;
                }

                if (char.IsUpper(c))
                {
                    bool prevIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    bool prevIsUpper = i > 0 && char.IsUpper(name[i - 1]);
                    bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    // LowerLeg -> LOWER_LEG (小文字→大文字の境界)
                    // URLValue -> URL_VALUE (頭字語の終わり: 大文字連続の後に小文字が続く位置)
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_' && (prevIsLowerOrDigit || (prevIsUpper && nextIsLower)))
                    {
                        sb.Append('_');
                    }
                    sb.Append(c);
                }
                else
                {
                    sb.Append(char.ToUpperInvariant(c));
                }
            }

            // 末尾の '_' を除去
            while (sb.Length > 0 && sb[sb.Length - 1] == '_') sb.Length--;
            return sb.ToString();
        }
    }
}
