export default {
  async fetch(request) {
    return new Response("OverPanel — скоро здесь будет панель.", {
      headers: { "content-type": "text/plain; charset=utf-8" },
    });
  },
};