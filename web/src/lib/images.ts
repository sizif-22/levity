/**
 * Helper to compress and format images for sending to the AI from the mobile app.
 * Resizes images client-side before sending so they stay well under websocket/relay payload limits,
 * convert to JPEG (or PNG if transparent) with max dimension 1600px.
 */

export interface MobileImage {
  name: string;
  type: string;
  data: string; // base64 without data: URL prefix
}

const MAX_IMAGE_DIM = 1600;
const JPEG_QUALITY = 0.85;

/** Max total image size allowed to be sent in one prompt (10 MB). */
export const MAX_TOTAL_IMAGE_BYTES = 10 * 1024 * 1024;

/**
 * Resizes an image file/blob down if larger than MAX_IMAGE_DIM and returns a base64 encoded data string.
 */
export async function processImageFile(file: File): Promise<MobileImage> {
  const isPng = file.type === 'image/png';
  const outMime = isPng ? 'image/png' : 'image/jpeg';

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error('Failed to read image file'));
    reader.onload = () => {
      const img = new Image();
      img.onerror = () => reject(new Error('Failed to decode image'));
      img.onload = () => {
        let { width, height } = img;
        if (width > MAX_IMAGE_DIM || height > MAX_IMAGE_DIM) {
          if (width > height) {
            height = Math.round((height * MAX_IMAGE_DIM) / width);
            width = MAX_IMAGE_DIM;
          } else {
            width = Math.round((width * MAX_IMAGE_DIM) / height);
            height = MAX_IMAGE_DIM;
          }
        }

        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext('2d');
        if (!ctx) {
          reject(new Error('Could not get canvas context'));
          return;
        }

        // Fill white background for JPEGs if needed
        if (outMime === 'image/jpeg') {
          ctx.fillStyle = '#FFFFFF';
          ctx.fillRect(0, 0, width, height);
        }

        ctx.drawImage(img, 0, 0, width, height);
        const dataUrl = canvas.toDataURL(outMime, JPEG_QUALITY);
        const comma = dataUrl.indexOf(',');
        const base64 = comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl;

        resolve({
          name: file.name || 'image.jpg',
          type: outMime,
          data: base64,
        });
      };
      img.src = reader.result as string;
    };
    reader.readAsDataURL(file);
  });
}
