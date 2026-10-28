<!-- SPDX-License-Identifier: MIT -->
<!-- Four-digit PIN entry: one real input drawn as four boxes. -->
<script setup lang="ts">
import { computed, ref } from 'vue'

const props = withDefaults(
  defineProps<{ modelValue: string; id?: string; disabled?: boolean; invalid?: boolean; label?: string }>(),
  { id: 'pin', disabled: false, invalid: false, label: 'PIN' },
)
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const focused = ref(false)
const input = ref<HTMLInputElement | null>(null)
const digits = computed(() => [0, 1, 2, 3].map((i) => props.modelValue[i] ?? ''))
const active = computed(() => Math.min(props.modelValue.length, 3))

function onInput(e: Event): void {
  const el = e.target as HTMLInputElement
  const clean = el.value.replace(/\D/g, '').slice(0, 4)
  // Keep the DOM in step even when the model value does not change (letters typed).
  el.value = clean
  emit('update:modelValue', clean)
}

defineExpose({ focus: () => input.value?.focus() })
</script>

<template>
  <div :class="['pin', { focused, disabled, invalid }]">
    <div v-for="(d, i) in digits" :key="i" :class="['box', { filled: d !== '', current: focused && i === active }]">
      {{ d }}
    </div>
    <input
      :id="id"
      ref="input"
      name="pin"
      inputmode="numeric"
      autocomplete="one-time-code"
      maxlength="4"
      spellcheck="false"
      :aria-label="label"
      :aria-invalid="invalid"
      :disabled="disabled"
      :value="modelValue"
      @input="onInput"
      @focus="focused = true"
      @blur="focused = false"
    />
  </div>
</template>

<style scoped>
.pin {
  position: relative;
  display: inline-grid;
  grid-template-columns: repeat(4, 48px);
  gap: 10px;
}

.box {
  height: 56px;
  border-radius: var(--radius);
  background: var(--fill);
  border: 2px solid transparent;
  display: grid;
  place-items: center;
  font-variant-numeric: tabular-nums;
  font-size: 24px;
  font-weight: 600;
  color: var(--text);
  transition:
    border-color 0.16s var(--ease),
    box-shadow 0.16s var(--ease),
    background 0.16s var(--ease);
}

.pin:hover:not(.disabled) .box:not(.current) {
  background: var(--fill-hover);
}

.box.current {
  border-color: var(--primary);
  box-shadow: 0 0 0 3px var(--primary-soft);
  background: var(--fill);
}

.invalid .box {
  background: var(--danger-soft);
}

.invalid .box.current {
  border-color: var(--danger);
  box-shadow: 0 0 0 3px var(--danger-soft);
}

.disabled .box {
  opacity: 0.5;
}

input {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: text;
  border: 0;
  padding: 0;
}

input:disabled {
  cursor: not-allowed;
}
</style>
