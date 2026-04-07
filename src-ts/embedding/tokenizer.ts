const CLS_TOKEN_ID = 101;
const SEP_TOKEN_ID = 102;
const UNK_TOKEN_ID = 100;
const MAX_SEQ_LEN = 512;
const MAX_INPUT_CHARS_PER_WORD = 100;

interface TokenizerVocab {
  [token: string]: number;
}

export class WordPieceTokenizer {
  private readonly vocab: TokenizerVocab;

  constructor(vocab: TokenizerVocab) {
    this.vocab = Object.create(null) as TokenizerVocab;
    Object.assign(this.vocab, vocab);
  }

  tokenize(text: string): number[] {
    const normalized = this.normalize(text);
    const words = this.preTokenize(normalized);
    const ids: number[] = [CLS_TOKEN_ID];

    for (const word of words) {
      if (ids.length >= MAX_SEQ_LEN - 1) break;
      const subIds = this.wordPiece(word);
      for (const id of subIds) {
        ids.push(id);
        if (ids.length >= MAX_SEQ_LEN - 1) break;
      }
    }

    ids.push(SEP_TOKEN_ID);
    return ids;
  }

  private normalize(text: string): string {
    let out = "";
    for (const ch of text) {
      const cp = ch.codePointAt(0)!;
      if (isControl(cp) && !isWhitespace(cp)) continue;
      if (isCjk(cp)) {
        out += ` ${ch} `;
      } else {
        out += ch;
      }
    }
    return out.toLowerCase();
  }

  private preTokenize(text: string): string[] {
    const tokens: string[] = [];
    let current = "";

    for (const ch of text) {
      const cp = ch.codePointAt(0)!;
      if (isWhitespace(cp)) {
        if (current) tokens.push(current);
        current = "";
      } else if (isPunctuation(cp)) {
        if (current) tokens.push(current);
        tokens.push(ch);
        current = "";
      } else {
        current += ch;
      }
    }

    if (current) tokens.push(current);
    return tokens;
  }

  private wordPiece(word: string): number[] {
    if (word.length > MAX_INPUT_CHARS_PER_WORD) return [UNK_TOKEN_ID];

    const ids: number[] = [];
    let start = 0;

    while (start < word.length) {
      let end = word.length;
      let matched = false;

      while (start < end) {
        const substr =
          start === 0 ? word.slice(0, end) : `##${word.slice(start, end)}`;
        const id = this.vocab[substr];
        if (id !== undefined) {
          ids.push(id);
          start = end;
          matched = true;
          break;
        }
        end--;
      }

      if (!matched) {
        return [UNK_TOKEN_ID];
      }
    }

    return ids;
  }
}

function isWhitespace(cp: number): boolean {
  return cp === 0x20 || cp === 0x09 || cp === 0x0a || cp === 0x0d;
}

function isControl(cp: number): boolean {
  if (cp === 0x09 || cp === 0x0a || cp === 0x0d) return false;
  const cat = charCategory(cp);
  return cat === "Cc" || cat === "Cf";
}

function isPunctuation(cp: number): boolean {
  if (
    (cp >= 33 && cp <= 47) ||
    (cp >= 58 && cp <= 64) ||
    (cp >= 91 && cp <= 96) ||
    (cp >= 123 && cp <= 126)
  ) {
    return true;
  }
  return /^\p{P}$/u.test(String.fromCodePoint(cp));
}

function isCjk(cp: number): boolean {
  return (
    (cp >= 0x4e00 && cp <= 0x9fff) ||
    (cp >= 0x3400 && cp <= 0x4dbf) ||
    (cp >= 0x20000 && cp <= 0x2a6df) ||
    (cp >= 0x2a700 && cp <= 0x2b73f) ||
    (cp >= 0x2b740 && cp <= 0x2b81f) ||
    (cp >= 0x2b820 && cp <= 0x2ceaf) ||
    (cp >= 0xf900 && cp <= 0xfaff) ||
    (cp >= 0x2f800 && cp <= 0x2fa1f)
  );
}

function charCategory(cp: number): string {
  if (cp <= 0x1f || (cp >= 0x7f && cp <= 0x9f)) return "Cc";
  if (
    cp === 0xad ||
    (cp >= 0x600 && cp <= 0x605) ||
    cp === 0x61c ||
    cp === 0x6dd ||
    cp === 0x70f
  )
    return "Cf";
  if (cp === 0xfeff || (cp >= 0xfff9 && cp <= 0xfffb)) return "Cf";
  if (cp >= 0x200b && cp <= 0x200f) return "Cf";
  if (cp >= 0x202a && cp <= 0x202e) return "Cf";
  if (cp >= 0x2060 && cp <= 0x2064) return "Cf";
  if (cp >= 0x2066 && cp <= 0x2069) return "Cf";
  return "Lo";
}
