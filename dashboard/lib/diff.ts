// Plain word-level diff (LCS-based) — no external dependency. A tailored
// resume is at most a few hundred words, so an O(n*m) DP table is trivial
// (tens of thousands of cells), and this avoids pulling in a diff library
// for something this small and this core to the Queue detail page.

export type DiffOp = { type: "equal" | "add" | "del"; text: string };

function tokenize(text: string): string[] {
  // Split into words and whitespace runs, keeping both as separate tokens so
  // rejoining every token's text reconstructs the original string exactly.
  return text.match(/\s+|\S+/g) ?? [];
}

export function diffWords(oldText: string, newText: string): DiffOp[] {
  const a = tokenize(oldText);
  const b = tokenize(newText);
  const n = a.length;
  const m = b.length;

  const dp: Int32Array[] = new Array(n + 1);
  for (let i = 0; i <= n; i++) dp[i] = new Int32Array(m + 1);

  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i][j] =
        a[i] === b[j]
          ? dp[i + 1][j + 1] + 1
          : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }

  const ops: DiffOp[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      ops.push({ type: "equal", text: a[i] });
      i++;
      j++;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) {
      ops.push({ type: "del", text: a[i] });
      i++;
    } else {
      ops.push({ type: "add", text: b[j] });
      j++;
    }
  }
  while (i < n) {
    ops.push({ type: "del", text: a[i] });
    i++;
  }
  while (j < m) {
    ops.push({ type: "add", text: b[j] });
    j++;
  }
  return ops;
}
