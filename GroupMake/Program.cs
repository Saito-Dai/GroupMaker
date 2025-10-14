using System;
using System.Collections.Generic;
using System.Linq;

namespace Grouping
{
    public static class Program
    {
        public static void Main(string[] args)
        {

            // ===== 使用例と使い方 =====
            // コンソールの指示に従って数値の入力をしてください。　
            // GroupAssignmentGenerator で R ラウンド分のグルーピングを生成します。
            //
            // For Teacher:
            //   乱数シードとは、同じシード値を渡すことで同じシャッフル結果＝同じグルーピングを再現できる仕組みです。
            //   実行結果を消しても、同じシードで再実行すれば結果を再確認できます。
            //
            // ベストエフォート設計:
            //   条件(同一グループの再出現回避、5-core回避、サイズ制約など)が厳しい場合、
            //   ローカルスワップで解消しきれず衝突(コンフリクト)が残る可能性があります。
            //   その場合は「最小衝突案」を採用して前進します(必要なら再実行)。

            // ===== ここから「コンソール入力」方式 =====
            Console.WriteLine("=== グループ編成ジェネレータ ===");
            Console.WriteLine("人数(N)・ラウンド数(R)・乱数シード(seed)を順に入力してください。");
            Console.WriteLine("※ EnterだけでN=54, R=3、seedはランダムになります。\n");

            int N = ReadInt("人数　(3以上推奨) [既定=54]", min: 3, max: int.MaxValue, defaultValue: 54);
            int R = ReadInt("ラウンド数 (1以上) [既定=3]", min: 1, max: int.MaxValue, defaultValue: 3);
            int? seed = ReadOptionalSeed("乱数シード 　桁数の制限はありません。（未入力=ランダム）[例: 12345]");

            // 生成器を用意(シードを渡すと再現性のあるランダムに)
            var generator = new GroupAssignmentGenerator(seed);

            // R ラウンド分のグルーピングを生成
            var rounds = generator.GenerateAllRounds(N, R);

            // ===== コンソール表示 =====
            for (int round = 0; round < rounds.Count; round++)
            {
                Console.WriteLine($"=== Round {round + 1} ===");
                int gidx = 1;
                foreach (var g in rounds[round])
                {
                    // グループ内は見やすく昇順表示、人数も併記
                    Console.WriteLine($"G{gidx++,2}: [" + string.Join(", ", g.OrderBy(x => x)) + $"] (人数={g.Count})");
                }
                Console.WriteLine();
            }
        }

        // --- 入力ユーティリティ ---
        private static int ReadInt(string prompt, int min, int max, int defaultValue)
        {
            while (true)
            {
                Console.Write($"{prompt}: ");
                var line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return defaultValue;
                if (int.TryParse(line, out var v) && v >= min && v <= max) return v;
                Console.WriteLine($"無効な入力です。{min}〜{max} の整数で入力してください。既定値を使うなら Enter のみ。");
            }
        }

        private static int? ReadOptionalSeed(string prompt)
        {
            while (true)
            {
                Console.Write($"{prompt}: ");
                var line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return null;          // 未入力=ランダム
                if (int.TryParse(line, out var s)) return s;               // 数値なら採用
                Console.WriteLine("無効な入力です。整数で入力するか、未入力(Enter)でランダムにしてください。");
            }
        }
    }

    public class GroupAssignmentGenerator
    {
        // ===== グルーピングの中核クラス =====
        // ・乱数シャッフル (Fisher-Yates)
        // ・5人分割 + 余り処理
        // ・過去のグループ履歴の保存(完全一致/5-core)
        // ・衝突(履歴との矛盾やサイズ違反)検出とローカルスワップ修復

        private readonly Random _random;

        // これまで出た「グループそのもの」のキー(昇順ゼロ埋め連結)を保存
        private readonly HashSet<string> _historyExact = new HashSet<string>();

        // これまで出た「5人組」のキーを保存(6人組の中の5人=5-coreの再出現を防ぐため)
        private readonly HashSet<string> _historyFiveCore = new HashSet<string>();

        public GroupAssignmentGenerator(int? seed = null)
        {
            // シードあり: 再現可能な乱数列 / シードなし: 実行ごとに異なる結果
            _random = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        /// <summary>
        /// N人をRラウンド分、各ラウンドで3〜6人のグループに分割した結果を返す。
        /// 同一グループ(完全一致)や5-core(6人組内の5人)の再出現をできるだけ回避。
        /// </summary>
        public List<List<List<int>>> GenerateAllRounds(int N, int R)
        {
            if (N < 3) throw new ArgumentException("N must be >= 3");
            if (R < 1) throw new ArgumentException("R must be >= 1");

            var allRounds = new List<List<List<int>>>();

            // 余りメンバーを既存5人組へ合流させる際の「合流開始位置」をラウンドごとにずらす
            // (毎回同じ先頭グループに偏らないようにするローテーション)
            int extraStart = 0;

            for (int round = 0; round < R; round++)
            {
                bool success = false;               // 衝突ゼロで確定できたか
                List<List<int>> bestGroups = null!; // 衝突数が最小だった案の保存(保険)
                int bestConflict = int.MaxValue;    // そのときの衝突数

                // 1ラウンドについて最大10回まで案を試す(乱数シャッフル→余り処理→スワップ修復)
                for (int attempt = 0; attempt < 10 && !success; attempt++)
                {
                    // 1..N のIDリストを作り、Fisher-Yatesで等確率シャッフル
                    var ids = Enumerable.Range(1, N).ToList();
                    FisherYatesShuffle(ids);

                    // 5人ずつ切り出し + 余り(remainder)取得
                    var (groups, remainder) = ChunkByFive(ids);

                    // 余りをルールに従って合流/独立グループ化
                    DistributeRemainder(groups, remainder, extraStart);

                    // 履歴と衝突しないよう、ローカルスワップで修復を試みる
                    var fixedOk = FixConflicts(groups, out int conflictCount);

                    // ベスト案を更新(衝突数がより少ないものを保持)
                    if (conflictCount < bestConflict)
                    {
                        bestConflict = conflictCount;
                        bestGroups = CloneGroups(groups);
                    }

                    if (fixedOk)
                    {
                        // 衝突ゼロで確定できたので、この案を採用
                        success = true;

                        // 採用したグループを履歴に登録(次ラウンドの衝突判定に使用)
                        RegisterHistory(groups);

                        // 余り合流の開始位置を次ラウンドに向けてローテーション
                        extraStart = groups.Count == 0 ? 0 : (extraStart + 1) % groups.Count;

                        // 採用案を保存(ディープコピー)
                        allRounds.Add(CloneGroups(groups));
                    }
                }

                if (!success)
                {
                    // 10回の試行で衝突ゼロにできなかった場合は
                    // 衝突数が最小だった案(best-effort)を採用して前進する
                    RegisterHistory(bestGroups);
                    extraStart = bestGroups.Count == 0 ? 0 : (extraStart + 1) % bestGroups.Count;
                    allRounds.Add(CloneGroups(bestGroups));
                }
            }

            return allRounds;
        }

        // ===================== メンバーのグループ化(5人切り出し) =====================

        /// <summary>
        /// シャッフル済みのID列から先頭5つずつグループ化し、余りを返す。
        /// ここでは「5人組のみ」を作り、サイズ3〜6の調整は余り処理で行う。
        /// </summary>
        private static (List<List<int>> groups, List<int> remainder) ChunkByFive(List<int> ids)
        {
            var groups = new List<List<int>>();
            int idx = 0;

            // 5人単位で切り出し
            while (idx + 5 <= ids.Count)
            {
                groups.Add(ids.GetRange(idx, 5));
                idx += 5;
            }

            // 5で割り切れない残り
            var remainder = ids.GetRange(idx, ids.Count - idx);
            return (groups, remainder);
        }

        /// <summary>
        /// 余りメンバーを既存の5人組へ合流(6人上限)させる or 独立グループで確定する。
        /// ・r=0: 何もしない
        /// ・r=1/2: 既存5人組へ均等合流(満杯が多いときは救済で新規3人組を作る)
        /// ・r=3/4: 余りのみで独立グループ
        /// ・r=8/9: 5人 + (3 or 4) に分割(安全弁; 現行の切り出しでは通常は出ない)
        /// ・その他: 3〜6に収まるよう貪欲にまとめる(将来拡張の保険)
        /// </summary>
        private void DistributeRemainder(List<List<int>> groups, List<int> remainder, int extraStart)
        {
            int r = remainder.Count;
            if (r == 0) return; // 余りなし

            // 特例: groups が空(= N < 5 のケースなど)では、3〜6のいずれかにちょうど合えばそのまま作る
            if (groups.Count == 0)
            {
                if (r == 3 || r == 4 || r == 5 || r == 6)
                {
                    groups.Add(new List<int>(remainder));
                    return;
                }
                // ここに来る r は(通常運用では)想定外だが、下の switch/default でフォールバックする
            }

            switch (r)
            {
                case 1:
                case 2:
                    // 余り1/2人は、既存5人組に「6人を超えない範囲」で均等合流
                    // ptr は合流開始位置で、ラウンドごとに extraStart により回す
                    int ptr = extraStart % Math.Max(1, groups.Count);

                    foreach (var member in remainder)
                    {
                        int attempts = 0;
                        bool placed = false;

                        // 全グループを最大1周して空き(6人未満)を探す
                        while (attempts < groups.Count)
                        {
                            var g = groups[ptr];
                            if (g.Count < 6)
                            {
                                g.Add(member);
                                placed = true;
                                ptr = (ptr + 1) % groups.Count; // 次の合流先をずらす
                                break;
                            }
                            ptr = (ptr + 1) % groups.Count;
                            attempts++;
                        }

                        if (!placed)
                        {
                            // すべて満杯(=全て6人)のような例外的状況:
                            // 新規3人組を作るために、人数の多いグループから2人借りる救済を試みる
                            var newGroup = new List<int> { member };

                            // グループを人数降順に並べ、末尾から1人ずつ借りる
                            var candidates = groups
                                .Select((g, i) => (g, i))
                                .OrderByDescending(t => t.g.Count)
                                .ToList();

                            foreach (var (g, idx) in candidates)
                            {
                                if (newGroup.Count >= 3) break; // 3人に達したら終了
                                if (g.Count > 3)               // 借り元も3人未満にならないように
                                {
                                    newGroup.Add(g[^1]);       // 末尾を1人借りる
                                    g.RemoveAt(g.Count - 1);
                                }
                            }

                            // 3〜6に収まれば採用、収まらなければ一旦追加(後続の修復で整える)
                            if (newGroup.Count >= 3 && newGroup.Count <= 6)
                            {
                                groups.Add(newGroup);
                            }
                            else
                            {
                                groups.Add(newGroup); // 最終手段(後でFixConflictsが対処)
                            }
                        }
                    }
                    break;

                case 3:
                case 4:
                    // 余りだけで独立グループ(サイズ要件クリア)
                    groups.Add(new List<int>(remainder));
                    break;

                case 8:
                case 9:
                    // 安全弁: 5人 + (3 or 4) に分ける
                    // (現行の 5人切り出し→余り では基本的に r は 0..4 に収まる想定)
                    var g5 = remainder.Take(5).ToList();
                    var g34 = remainder.Skip(5).ToList();
                    groups.Add(g5);
                    groups.Add(g34);
                    break;

                default:
                    // フォールバック: 3〜6に収まるように貪欲に詰めていく
                    // (将来「5人切り出し」以外の戦略を導入した場合の保険)
                    int i = 0;
                    while (i < remainder.Count)
                    {
                        int left = remainder.Count - i;                 // 残り人数
                        int take = Math.Min(6, Math.Max(3, left));      // 3〜6の範囲で可能なだけ取る
                        // ※ take > left にならないよう left を上限にしている
                        take = Math.Min(take, left);

                        groups.Add(remainder.GetRange(i, take));
                        i += take;
                    }
                    break;
            }
        }

        /// <summary>
        /// Fisher-Yates シャッフル: すべての順列が等確率になる正統派シャッフル。
        /// </summary>
        private void FisherYatesShuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]); // タプル代入でスワップ
            }
        }

        // ===================== 衝突(コンフリクト)の検出と修復 =====================

        /// <summary>
        /// 現在のグループ案に対して、履歴との衝突やサイズ違反がないかをチェックし、
        /// 見つかった場合は「衝突を増やさない範囲」でランダムに1人交換するローカルスワップを繰り返して解消を試みる。
        /// </summary>
        private bool FixConflicts(List<List<int>> groups, out int conflictCount)
        {
            // まずは衝突のあるグループのインデックス一覧を取得
            var conflicts = GetConflictingGroupIndices(groups);
            conflictCount = conflicts.Count;
            if (conflicts.Count == 0) return true; // 衝突がなければ即成功

            // ローカルスワップを最大20回ほど試す(山登り法の簡易版)
            for (int tries = 0; tries < 20; tries++)
            {
                if (conflicts.Count == 0) return true;

                // 衝突しているグループaを1つ選び、交換相手b(別グループ)をランダムに選ぶ
                int aIdx = conflicts[_random.Next(conflicts.Count)];
                int bIdx = aIdx;
                for (int t = 0; t < 10 && bIdx == aIdx; t++)
                {
                    bIdx = _random.Next(groups.Count);
                }
                if (aIdx == bIdx) continue; // たまたま同じならスキップ

                var ga = groups[aIdx];
                var gb = groups[bIdx];
                if (ga.Count == 0 || gb.Count == 0) continue;

                // 各グループからランダムに1人選んで交換
                int ai = _random.Next(ga.Count);
                int bi = _random.Next(gb.Count);
                int aVal = ga[ai];
                int bVal = gb[bi];
                ga[ai] = bVal;
                gb[bi] = aVal;

                // スワップ後の衝突を再評価
                // ※簡易実装: 影響グループだけの再判定にしていない(全体を再評価)
                var newConflicts = GetConflictingGroupIndices(groups);

                if (newConflicts.Count <= conflicts.Count)
                {
                    // 悪化していなければ採用(=この状態をベースに次の試行へ)
                    conflicts = newConflicts;
                }
                else
                {
                    // 悪化したので元に戻す(リジェクト)
                    ga[ai] = aVal;
                    gb[bi] = bVal;
                }
            }

            // 試行後の最終判定
            conflicts = GetConflictingGroupIndices(groups);
            conflictCount = conflicts.Count;
            return conflicts.Count == 0;
        }

        /// <summary>
        /// 衝突(サイズ不正 / 完全一致の再出現 / 6人組における5-core再出現)しているグループの
        /// インデックスをリストで返す。
        /// </summary>
        private List<int> GetConflictingGroupIndices(List<List<int>> groups)
        {
            var bad = new List<int>();
            for (int i = 0; i < groups.Count; i++)
            {
                if (IsConflict(groups[i])) bad.Add(i);
            }
            return bad;
        }

        /// <summary>
        /// 単一グループについて、以下のいずれかに該当すれば衝突(true)と判定:
        /// ・サイズ違反(3未満 or 6超過)
        /// ・過去に完全一致で出たグループ(_historyExactに存在)
        /// ・6人組のとき、その中の任意5人(6通り)が過去に出た5人組(_historyFiveCoreに存在)
        /// </summary>
        private bool IsConflict(List<int> g)
        {
            // サイズ制約 (3〜6)
            if (g.Count < 3 || g.Count > 6) return true;

            // 完全一致の再出現チェック
            var key = KeyOf(g);
            if (_historyExact.Contains(key)) return true;

            // 6人組の場合は 6C5 = 6通りの5人部分集合についてもチェック(=5-coreの再出現禁止)
            if (g.Count == 6)
            {
                var sorted = g.OrderBy(x => x).ToArray();
                for (int excluded = 0; excluded < 6; excluded++)
                {
                    var subset = new List<int>(5);
                    for (int i = 0; i < 6; i++)
                    {
                        if (i == excluded) continue; // 1人だけ除外して5人を作る
                        subset.Add(sorted[i]);
                    }
                    var k = KeyOf(subset);
                    if (_historyFiveCore.Contains(k)) return true; // 5-core が過去に存在
                }
            }
            return false;
        }

        /// <summary>
        /// 採用したグループを履歴に登録。
        /// ・完全一致のキーは _historyExact へ
        /// ・サイズ5のグループは _historyFiveCore へ(以後の6人組の5-coreチェックに使う)
        /// </summary>
        private void RegisterHistory(List<List<int>> groups)
        {
            foreach (var g in groups)
            {
                var key = KeyOf(g);
                _historyExact.Add(key);
                if (g.Count == 5)
                {
                    _historyFiveCore.Add(key);
                }
            }
        }

        /// <summary>
        /// グループの「順序によらない同一性」を表すキーを作る。
        /// ・昇順ソート → 2桁ゼロ埋め文字列へ → '-' で連結
        /// 例) [3,10,2] → "02-03-10"
        /// </summary>
        private static string KeyOf(IEnumerable<int> g)
        {
            return string.Join("-", g.OrderBy(x => x).Select(x => x.ToString("D2")));
        }

        /// <summary>
        /// グループ集合のディープコピーを生成(履歴保存や返却時の安全性確保)
        /// </summary>
        private static List<List<int>> CloneGroups(List<List<int>> groups)
        {
            return groups.Select(g => g.ToList()).ToList();
        }
    }
}
