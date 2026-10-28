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
  height: 52px;
  border-radius: 3px;
  background: var(--input);
  border: 1px solid var(--line-strong);
  display: grid;
  place-items: center;
  font-family: var(--mono);
  font-size: 26px;
  font-weight: 700;
  color: #fff;
  transition:
    border-color 0.15s ease,
    box-shadow 0.15s ease;
}

.box.filled {
  border-color: #4f7ea3;
}

.box.current {
  border-color: var(--accent);
  box-shadow: 0 0 0 3px rgb(102 192 244 / 22%);
}

.invalid .box {
  border-color: var(--bad);
}

.disabled .box {
  opacity: 0.45;
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
