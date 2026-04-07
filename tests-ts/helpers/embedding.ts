export function mockEmbed(text: string): Float32Array {
  const vec = new Float32Array(384);
  let hash = 0;
  for (let i = 0; i < text.length; i++) {
    hash = (hash * 31 + text.charCodeAt(i)) | 0;
  }
  for (let i = 0; i < 384; i++) {
    hash = (hash * 1103515245 + 12345) | 0;
    vec[i] = ((hash >> 16) & 0x7fff) / 0x7fff - 0.5;
  }
  let norm = 0;
  for (let i = 0; i < 384; i++) norm += (vec[i] as number) * (vec[i] as number);
  norm = Math.sqrt(norm);
  for (let i = 0; i < 384; i++) vec[i] = (vec[i] as number) / norm;
  return vec;
}

export function mockEmbedSemantic(text: string): Float32Array {
  const words = text.toLowerCase().split(/\s+/);
  const vec = new Float32Array(384);
  for (const word of words) {
    const wordVec = mockEmbed(word);
    for (let i = 0; i < 384; i++) {
      vec[i] = (vec[i] as number) + (wordVec[i] as number) / words.length;
    }
  }
  let norm = 0;
  for (let i = 0; i < 384; i++) norm += (vec[i] as number) * (vec[i] as number);
  norm = Math.sqrt(norm);
  if (norm > 0) {
    for (let i = 0; i < 384; i++) vec[i] = (vec[i] as number) / norm;
  }
  return vec;
}
