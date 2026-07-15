export interface ModuleConfigSchema {
  moduleId: string;
  moduleName: string;
  options: ModuleConfigOption[];
}

export interface ModuleConfigOption {
  key: string;
  envVarName: string;
  defaultValue: string;
  type: ConfigOptionType;
  description: string;
  enumOptions?: string[] | null;
}

export enum ConfigOptionType {
  Boolean = 'Boolean',
  Number = 'Number',
  String = 'String',
  Enum = 'Enum',
  StringList = 'StringList'
}
