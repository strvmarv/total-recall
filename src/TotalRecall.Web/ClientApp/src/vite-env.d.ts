/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_TR_TOKEN?: string;
  readonly VITE_TR_BACKEND?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
