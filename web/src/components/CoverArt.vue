<!-- SPDX-License-Identifier: MIT -->
<!-- Soft gradient cover in the desk's colours (--cover-* from coverStyle) with a faint dot grid. -->
<template>
  <div class="art" aria-hidden="true" />
</template>

<style scoped>
.art {
  position: absolute;
  inset: 0;
  background:
    radial-gradient(circle at 1px 1px, rgb(0 0 0 / 6%) 1px, transparent 0) 0 0 / 16px 16px,
    radial-gradient(70% 140% at 92% 0%, color-mix(in srgb, var(--cover-ink, #006fee) 24%, transparent) 0%, transparent 70%),
    radial-gradient(60% 120% at 0% 100%, color-mix(in srgb, var(--cover-b, #e0e7ff) 90%, transparent) 0%, transparent 70%),
    linear-gradient(120deg, var(--cover-a, #dbeafe) 0%, var(--cover-b, #e0e7ff) 100%);
}

/* Dark theme: the desk colour as a tint over the card surface instead of a pastel. */
:root[data-theme='dark'] .art {
  background:
    radial-gradient(circle at 1px 1px, rgb(255 255 255 / 6%) 1px, transparent 0) 0 0 / 16px 16px,
    radial-gradient(70% 140% at 92% 0%, color-mix(in srgb, var(--cover-ink, #006fee) 45%, transparent) 0%, transparent 70%),
    linear-gradient(120deg, color-mix(in srgb, var(--cover-ink, #006fee) 26%, #222225) 0%, color-mix(in srgb, var(--cover-ink, #006fee) 10%, #222225) 100%);
}

@media (prefers-color-scheme: dark) {
  :root:not([data-theme='light']) .art {
    background:
      radial-gradient(circle at 1px 1px, rgb(255 255 255 / 6%) 1px, transparent 0) 0 0 / 16px 16px,
      radial-gradient(70% 140% at 92% 0%, color-mix(in srgb, var(--cover-ink, #006fee) 45%, transparent) 0%, transparent 70%),
      linear-gradient(120deg, color-mix(in srgb, var(--cover-ink, #006fee) 26%, #222225) 0%, color-mix(in srgb, var(--cover-ink, #006fee) 10%, #222225) 100%);
  }
}
</style>
