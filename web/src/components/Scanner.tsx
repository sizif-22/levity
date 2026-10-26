import { useEffect, useRef, useState } from 'react';
import jsQR from 'jsqr';

/**
 * In-app QR scanner. Scanning with the phone's Camera app opens the link in a separate browser sheet whose
 * storage the installed app cannot see, so pairing has to happen from inside the app that will keep the key.
 */
export function Scanner({ onResult, onCancel }: { onResult: (text: string) => void; onCancel: () => void }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const onResultRef = useRef(onResult);
  onResultRef.current = onResult;
  const [error, setError] = useState('');

  useEffect(() => {
    let stopped = false;
    let raf = 0;
    let stream: MediaStream | null = null;
    let last = 0;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });

    if (!navigator.mediaDevices?.getUserMedia || !ctx) {
      setError('This browser cannot open the camera here. Paste the link instead.');
      return;
    }

    navigator.mediaDevices
      .getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false })
      .then(async (s) => {
        if (stopped) { s.getTracks().forEach((t) => t.stop()); return; }
        stream = s;
        const video = videoRef.current!;
        video.srcObject = s;
        await video.play();

        const tick = (now: number) => {
          if (stopped) return;
          raf = requestAnimationFrame(tick);
          if (now - last < 90 || video.readyState < 2 || !video.videoWidth) return; // ~11 scans per second
          last = now;

          const scale = Math.min(1, 720 / video.videoWidth);
          canvas.width = Math.round(video.videoWidth * scale);
          canvas.height = Math.round(video.videoHeight * scale);
          ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
          const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
          const code = jsQR(image.data, image.width, image.height, { inversionAttempts: 'attemptBoth' });
          if (code?.data) {
            stopped = true;
            cancelAnimationFrame(raf);
            stream?.getTracks().forEach((t) => t.stop());
            onResultRef.current(code.data);
          }
        };
        raf = requestAnimationFrame(tick);
      })
      .catch((e: DOMException) => {
        setError(
          e.name === 'NotAllowedError'
            ? 'Camera access was blocked. Allow it for this site in your browser settings, or paste the link instead.'
            : e.name === 'NotFoundError'
              ? 'No camera was found. Paste the link instead.'
              : `Could not open the camera: ${e.message}`,
        );
      });

    return () => {
      stopped = true;
      cancelAnimationFrame(raf);
      stream?.getTracks().forEach((t) => t.stop());
    };
  }, []);

  return (
    <div className="scanner">
      <video ref={videoRef} playsInline muted autoPlay />
      <div className="scanner-frame" aria-hidden="true" />
      <p className="scanner-hint">{error || 'Point the camera at the QR code in Levity on your PC'}</p>
      <button className="btn ghost scanner-cancel" onClick={onCancel}>Cancel</button>
    </div>
  );
}
