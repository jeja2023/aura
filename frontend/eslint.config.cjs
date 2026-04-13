/* 文件：前端 ESLint 扁平配置 | File: Frontend ESLint flat config */
const js = require("@eslint/js");
const globals = require("globals");

module.exports = [
  { ignores: ["common/vendor/**"] },
  js.configs.recommended,
  {
    files: ["**/*.js"],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: "script",
      globals: {
        ...globals.browser,
        THREE: "readonly",
        echarts: "readonly",
      },
    },
    rules: {
      "no-unused-vars": [
        "warn",
        {
          argsIgnorePattern: "^_",
          varsIgnorePattern: "^_",
          caughtErrors: "none",
        },
      ],
    },
  },
];
