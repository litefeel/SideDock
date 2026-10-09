namespace SideDock;

internal static class BrowserIconScript
{
    internal static string Create(int maxBytes) => $$"""
        (async () => {
            if (location.protocol !== 'https:' && location.protocol !== 'http:') {
                return { error: 'UnsupportedOrigin' };
            }
            const controller = new AbortController();
            const timer = setTimeout(() => controller.abort(), 8000);
            try {
                const response = await fetch(new URL('/favicon.ico', location.origin).href, {
                    credentials: 'same-origin', mode: 'same-origin', redirect: 'error',
                    signal: controller.signal
                });
                if (!response.ok) return { status: response.status, error: 'HttpStatus' };
                if (Number(response.headers.get('Content-Length')) > {{maxBytes}}) {
                    await response.body?.cancel();
                    return { error: 'ByteLimit' };
                }
                if (!response.body) return { error: 'EmptyBody' };
                const reader = response.body.getReader();
                const chunks = [];
                let size = 0;
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    size += value.length;
                    if (size > {{maxBytes}}) {
                        await reader.cancel();
                        return { error: 'ByteLimit' };
                    }
                    chunks.push(value);
                }
                let binary = '';
                for (const chunk of chunks) {
                    for (let offset = 0; offset < chunk.length; offset += 32768) {
                        binary += String.fromCharCode(...chunk.subarray(offset, offset + 32768));
                    }
                }
                return { status: response.status, data: btoa(binary) };
            } catch {
                return { error: 'FetchFailed' };
            } finally {
                clearTimeout(timer);
            }
        })()
        """;
}
