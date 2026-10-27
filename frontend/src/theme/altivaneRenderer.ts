export type RendererAttributes = { nonce?: string };

const noncePattern = /^[A-Za-z0-9+/]{24}$|^[A-Za-z0-9+/]{23}=$|^[A-Za-z0-9+/]{22}==$/;

export function nonceMetaToRendererAttributes(meta: HTMLMetaElement | null | undefined): RendererAttributes {
  const value = meta?.getAttribute('content') ?? '';
  return value.length === 24 && noncePattern.test(value) && value !== '__CSP_NONCE__' ? { nonce: value } : {};
}

export function getRendererAttributes(documentRef: Document = document): RendererAttributes {
  return nonceMetaToRendererAttributes(documentRef.querySelector<HTMLMetaElement>('meta[name="csp-nonce"]'));
}

export const readNonceMeta = getRendererAttributes;
export const rendererAttributesFromDocument = getRendererAttributes;
